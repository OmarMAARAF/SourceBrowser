using System;
using System.Collections.Generic;
using System.Globalization;

namespace Microsoft.SourceBrowser.Common
{
    /// <summary>
    /// Decides which folder/assembly name a project should be indexed under when several projects
    /// (e.g. coming from different binlogs) share the same assembly short name.
    ///
    /// <para>
    /// The source browser addresses every assembly by its short name and writes its output into a
    /// folder named after that short name. Historically the first project with a given name "won"
    /// and every later project with the same name was silently dropped. When duplicates are allowed,
    /// later projects are instead relocated into a unique folder (e.g. <c>Foo</c>, <c>Foo_2</c>,
    /// <c>Foo_3</c>, ...) so all of them are indexed and browsable.
    /// </para>
    /// </summary>
    public static class AssemblyNameDeduplicator
    {
        /// <summary>
        /// Reserves and returns the name the project should be indexed under.
        /// </summary>
        /// <param name="assemblyName">The project's assembly short name.</param>
        /// <param name="reservedNames">
        /// The set of names already used in this run. The chosen name is added to this set so that
        /// subsequent calls don't collide with it.
        /// </param>
        /// <param name="allowDuplicates">
        /// When <see langword="false"/>, a project whose name is already reserved is skipped
        /// (this method returns <see langword="null"/>). When <see langword="true"/>, it is given a
        /// unique name derived from <paramref name="assemblyName"/>.
        /// </param>
        /// <returns>
        /// The name to index the project under, or <see langword="null"/> if the project should be
        /// skipped because its name is already taken and duplicates are not allowed.
        /// </returns>
        public static string Resolve(string assemblyName, ISet<string> reservedNames, bool allowDuplicates)
        {
            if (assemblyName == null)
            {
                throw new ArgumentNullException(nameof(assemblyName));
            }

            if (reservedNames == null)
            {
                throw new ArgumentNullException(nameof(reservedNames));
            }

            // Not seen before: use the name as-is. This is the common case and keeps the output
            // identical to how it was before duplicate handling existed.
            if (reservedNames.Add(assemblyName))
            {
                return assemblyName;
            }

            if (!allowDuplicates)
            {
                return null;
            }

            return GetUniqueName(assemblyName, reservedNames);
        }

        /// <summary>
        /// Produces (and reserves) a name of the form <c>{baseName}_{n}</c> that is not yet present
        /// in <paramref name="reservedNames"/>.
        /// </summary>
        public static string GetUniqueName(string baseName, ISet<string> reservedNames)
        {
            if (baseName == null)
            {
                throw new ArgumentNullException(nameof(baseName));
            }

            if (reservedNames == null)
            {
                throw new ArgumentNullException(nameof(reservedNames));
            }

            for (int i = 2; ; i++)
            {
                var candidate = baseName + "_" + i.ToString(CultureInfo.InvariantCulture);
                if (reservedNames.Add(candidate))
                {
                    return candidate;
                }
            }
        }
    }
}
