// Copyright (c) .NET Foundation and contributors. All rights reserved. Licensed under the Microsoft Reciprocal License. See LICENSE.TXT file in the project root for full license information.

namespace WixToolset.Core.Burn.Bundles
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Linq;
    using WixToolset.Data;
    using WixToolset.Data.Burn;
    using WixToolset.Data.Tuples;
    using WixToolset.Extensibility.Services;

    internal class OrderSearchesCommand
    {
        public OrderSearchesCommand(IMessaging messaging, IntermediateSection section)
        {
            this.Messaging = messaging;
            this.Section = section;
        }

        private IMessaging Messaging { get; }

        private IntermediateSection Section { get; }

        public IDictionary<string, IList<IntermediateTuple>> ExtensionSearchTuplesByExtensionId { get; private set; }

        public IList<ISearchFacade> OrderedSearchFacades { get; private set; }

        public void Execute()
        {
            this.ExtensionSearchTuplesByExtensionId = new Dictionary<string, IList<IntermediateTuple>>();
            this.OrderedSearchFacades = new List<ISearchFacade>();

            var searchRelationTuples = this.Section.Tuples.OfType<WixSearchRelationTuple>().ToList();
            var searchTuples = this.Section.Tuples.OfType<WixSearchTuple>().ToList();
            if (searchTuples.Count == 0)
            {
                // nothing to do!
                return;
            }

            var tupleDictionary = searchTuples.ToDictionary(t => t.Id.Id);

            var constraints = new Constraints();
            if (searchRelationTuples.Count > 0)
            {
                // add relational info to our data...
                foreach (var searchRelationTuple in searchRelationTuples)
                {
                    constraints.AddConstraint(searchRelationTuple.Id.Id, searchRelationTuple.ParentSearchRef);
                }
            }

            this.FindCircularReference(constraints);

            if (this.Messaging.EncounteredError)
            {
                return;
            }

            this.FlattenDependentReferences(constraints);

            // Reorder by topographical sort (http://en.wikipedia.org/wiki/Topological_sorting)
            // We use a variation of Kahn (1962) algorithm as described in
            // Wikipedia, with the additional criteria that start nodes are sorted
            // lexicographically at each step to ensure a deterministic ordering
            // based on 'after' dependencies and ID.
            var sorter = new TopologicalSort();
            var sortedIds = sorter.Sort(tupleDictionary.Keys, constraints);

            // Now, create the search facades with the searches in order...
            this.OrderSearches(sortedIds, tupleDictionary);
        }

        /// <summary>
        /// A dictionary of constraints, mapping an id to a list of ids.
        /// </summary>
        private class Constraints : Dictionary<string, List<string>>
        {
            public void AddConstraint(string id, string afterId)
            {
                if (!this.ContainsKey(id))
                {
                    this.Add(id, new List<string>());
                }

                // TODO: Show warning if a constraint is seen twice?
                if (!this[id].Contains(afterId))
                {
                    this[id].Add(afterId);
                }
            }

            // TODO: Hide other Add methods?
        }

        /// <summary>
        /// Finds circular references in the constraints.
        /// </summary>
        /// <param name="constraints">Constraints to check.</param>
        /// <remarks>This is not particularly performant, but it works.</remarks>
        private void FindCircularReference(Constraints constraints)
        {
            foreach (string id in constraints.Keys)
            {
                var seenIds = new List<string>();
                string chain = null;
                if (this.FindCircularReference(constraints, id, id, seenIds, out chain))
                {
                    // We will show a separate message for every ID that's in
                    // the loop. We could bail after the first one, but then
                    // we wouldn't catch disjoint loops in a single run.
                    this.Messaging.Write(ErrorMessages.CircularSearchReference(chain));
                }
            }
        }

        /// <summary>
        /// Recursive function that finds circular references in the constraints.
        /// </summary>
        /// <param name="constraints">Constraints to check.</param>
        /// <param name="checkId">The identifier currently being looking for. (Fixed across a given run.)</param>
        /// <param name="currentId">The idenifier curently being tested.</param>
        /// <param name="seenIds">A list of identifiers seen, to ensure each identifier is only expanded once.</param>
        /// <param name="chain">If a circular reference is found, will contain the chain of references.</param>
        /// <returns>True if a circular reference is found, false otherwise.</returns>
        private bool FindCircularReference(Constraints constraints, string checkId, string currentId, List<string> seenIds, out string chain)
        {
            chain = null;
            if (constraints.TryGetValue(currentId, out var afterList))
            {
                foreach (string afterId in afterList)
                {
                    if (afterId == checkId)
                    {
                        chain = String.Format(CultureInfo.InvariantCulture, "{0} -> {1}", currentId, afterId);
                        return true;
                    }

                    if (!seenIds.Contains(afterId))
                    {
                        seenIds.Add(afterId);
                        if (this.FindCircularReference(constraints, checkId, afterId, seenIds, out chain))
                        {
                            chain = String.Format(CultureInfo.InvariantCulture, "{0} -> {1}", currentId, chain);
                            return true;
                        }
                    }
                }
            }

            return false;
        }

        /// <summary>
        /// Flattens any dependency chains to simplify reordering.
        /// </summary>
        /// <param name="constraints"></param>
        private void FlattenDependentReferences(Constraints constraints)
        {
            foreach (string id in constraints.Keys)
            {
                var flattenedIds = new List<string>();
                this.AddDependentReferences(constraints, id, flattenedIds);
                var constraintList = constraints[id];
                foreach (var flattenedId in flattenedIds)
                {
                    if (!constraintList.Contains(flattenedId))
                    {
                        constraintList.Add(flattenedId);
                    }
                }
            }
        }

        /// <summary>
        /// Adds dependent references to a list.
        /// </summary>
        /// <param name="constraints"></param>
        /// <param name="currentId"></param>
        /// <param name="seenIds"></param>
        private void AddDependentReferences(Constraints constraints, string currentId, List<string> seenIds)
        {
            if (constraints.TryGetValue(currentId, out var afterList))
            {
                foreach (var afterId in afterList)
                {
                    if (!seenIds.Contains(afterId))
                    {
                        seenIds.Add(afterId);
                        this.AddDependentReferences(constraints, afterId, seenIds);
                    }
                }
            }
        }

        /// <summary>
        /// Reorder by topological sort
        /// </summary>
        /// <remarks>
        /// We use a variation of Kahn (1962) algorithm as described in
        /// Wikipedia (http://en.wikipedia.org/wiki/Topological_sorting), with
        /// the additional criteria that start nodes are sorted lexicographically
        /// at each step to ensure a deterministic ordering based on 'after'
        /// dependencies and ID.
        /// </remarks>
        private class TopologicalSort
        {
            private readonly List<string> startIds = new List<string>();
            private Constraints constraints;

            /// <summary>
            /// Reorder by topological sort
            /// </summary>
            /// <param name="allIds">The complete list of IDs.</param>
            /// <param name="constraints">Constraints to use.</param>
            /// <returns>The topologically sorted list of IDs.</returns>
            internal List<string> Sort(IEnumerable<string> allIds, Constraints constraints)
            {
                this.startIds.Clear();
                this.CopyConstraints(constraints);

                this.FindInitialStartIds(allIds);

                // We always create a new sortedId list, because we return it
                // to the caller and don't know what its lifetime may be.
                var sortedIds = new List<string>();

                while (this.startIds.Count > 0)
                {
                    this.SortStartIds();

                    var currentId = this.startIds[0];
                    sortedIds.Add(currentId);
                    this.startIds.RemoveAt(0);

                    this.ResolveConstraint(currentId);
                }

                return sortedIds;
            }

            /// <summary>
            /// Copies a Constraints set (to prevent modifying the incoming data).
            /// </summary>
            /// <param name="constraints">Constraints to copy.</param>
            private void CopyConstraints(Constraints constraints)
            {
                this.constraints = new Constraints();
                foreach (var id in constraints.Keys)
                {
                    foreach (var afterId in constraints[id])
                    {
                        this.constraints.AddConstraint(id, afterId);
                    }
                }
            }

            /// <summary>
            /// Finds initial start IDs.  (Those with no constraints.)
            /// </summary>
            /// <param name="allIds">The complete list of IDs.</param>
            private void FindInitialStartIds(IEnumerable<string> allIds)
            {
                foreach (var id in allIds)
                {
                    if (!this.constraints.ContainsKey(id))
                    {
                        this.startIds.Add(id);
                    }
                }
            }

            /// <summary>
            /// Sorts start IDs.
            /// </summary>
            private void SortStartIds()
            {
                this.startIds.Sort();
            }

            /// <summary>
            /// Removes the resolved constraint and updates the list of startIds
            /// with any now-valid (all constraints resolved) IDs.
            /// </summary>
            /// <param name="resolvedId">The ID to resolve from the set of constraints.</param>
            private void ResolveConstraint(string resolvedId)
            {
                var newStartIds = new List<string>();

                foreach (var id in this.constraints.Keys)
                {
                    if (this.constraints[id].Contains(resolvedId))
                    {
                        this.constraints[id].Remove(resolvedId);

                        // If we just removed the last constraint for this
                        // ID, it is now a valid start ID.
                        if (this.constraints[id].Count == 0)
                        {
                            newStartIds.Add(id);
                        }
                    }
                }

                foreach (var id in newStartIds)
                {
                    this.constraints.Remove(id);
                }

                this.startIds.AddRange(newStartIds);
            }
        }

        private void OrderSearches(List<string> sortedIds, Dictionary<string, WixSearchTuple> searchTupleDictionary)
        {
            // TODO: Although the WixSearch tables are defined in the Util extension,
            // the Bundle Binder has to know all about them. We hope to revisit all
            // of this in the 4.0 timeframe.
            var legacySearchesById = this.Section.Tuples
                .Where(t => t.Definition.Type == TupleDefinitionType.WixComponentSearch ||
                       t.Definition.Type == TupleDefinitionType.WixFileSearch ||
                       t.Definition.Type == TupleDefinitionType.WixProductSearch ||
                       t.Definition.Type == TupleDefinitionType.WixRegistrySearch)
                .ToDictionary(t => t.Id.Id);
            var setVariablesById = this.Section.Tuples
                .OfType<WixSetVariableTuple>()
                .ToDictionary(t => t.Id.Id);
            var extensionSearchesById = this.Section.Tuples
                .Where(t => t.Definition.HasTag(BurnConstants.BundleExtensionSearchTupleDefinitionTag))
                .ToDictionary(t => t.Id.Id);

            foreach (var searchId in sortedIds)
            {
                var searchTuple = searchTupleDictionary[searchId];
                if (legacySearchesById.TryGetValue(searchId, out var specificSearchTuple))
                {
                    this.OrderedSearchFacades.Add(new LegacySearchFacade(searchTuple, specificSearchTuple));
                }
                else if (setVariablesById.TryGetValue(searchId, out var setVariableTuple))
                {
                    this.OrderedSearchFacades.Add(new SetVariableSearchFacade(searchTuple, setVariableTuple));
                }
                else if (extensionSearchesById.TryGetValue(searchId, out var extensionSearchTuple))
                {
                    this.OrderedSearchFacades.Add(new ExtensionSearchFacade(searchTuple));

                    if (!this.ExtensionSearchTuplesByExtensionId.TryGetValue(searchTuple.BundleExtensionRef, out var extensionSearchTuples))
                    {
                        extensionSearchTuples = new List<IntermediateTuple>();
                        this.ExtensionSearchTuplesByExtensionId[searchTuple.BundleExtensionRef] = extensionSearchTuples;
                    }
                    extensionSearchTuples.Add(extensionSearchTuple);
                }
                else
                {
                    this.Messaging.Write(ErrorMessages.MissingBundleSearch(searchTuple.SourceLineNumbers, searchId));
                }
            }
        }
    }
}
