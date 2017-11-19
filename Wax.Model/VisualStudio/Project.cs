﻿namespace tomenglertde.Wax.Model.VisualStudio
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Diagnostics.CodeAnalysis;
    using System.IO;
    using System.Linq;
    using System.Runtime.InteropServices;

    using Equatable;

    using JetBrains.Annotations;

    using Mono.Cecil;

    [ImplementsEquatable]
    public class Project
    {
        [NotNull]
        private readonly EnvDTE.Project _project;
        private readonly VSLangProj.VSProject _vsProject;
        [NotNull, ItemNotNull]
        private readonly ICollection<Project> _referencedBy = new HashSet<Project>();

        [NotNull]
        private readonly string _projectTypeGuids;

        public Project([NotNull] Solution solution, [NotNull] EnvDTE.Project project)
        {
            Solution = solution;
            _project = project;
            _vsProject = project.TryGetObject() as VSLangProj.VSProject;

            Debug.Assert(_project.UniqueName != null);
            UniqueName = _project.UniqueName;

            _projectTypeGuids = _project.GetProjectTypeGuids();

            PrimaryOutputFileName = _project.ConfigurationManager?.ActiveConfiguration?.OutputGroups?.Item(BuildFileGroups.Built.ToString())?.GetFileNames().FirstOrDefault();
        }

        [NotNull, ItemNotNull]
        public IReadOnlyCollection<ProjectReference> GetProjectReferences()
        {
            return GetProjectReferences(GetReferences());
        }

        [NotNull, ItemNotNull]
        private IReadOnlyCollection<ProjectReference> GetProjectReferences([NotNull, ItemNotNull] IEnumerable<VSLangProj.Reference> references)
        {
            var projectReferences = references
                .Where(reference => reference.GetSourceProject() != null)
                .Where(reference => reference.CopyLocal)
                .Select(reference => new ProjectReference(Solution, reference));

            return projectReferences.ToArray();
        }

        [NotNull, ItemNotNull]
        private static IReadOnlyCollection<ProjectOutput> GetLocalFileReferences([NotNull] Project rootProject, bool deployExternalLocalizations, [NotNull, ItemNotNull] IReadOnlyCollection<VSLangProj.Reference> references, [NotNull] string targetDirectory)
        {
            var localFileReferences = references
                .Where(reference => reference.GetSourceProject() == null)
                .Where(reference => reference.CopyLocal)
                .Where(reference => !string.IsNullOrEmpty(reference.Path))
                .Select(reference => new ProjectOutput(rootProject, reference, targetDirectory))
                .Concat(GetSecondTierReferences(references, rootProject, deployExternalLocalizations, targetDirectory));

            return localFileReferences.ToArray();
        }

        [NotNull, ItemNotNull]
        public ICollection<Project> ReferencedBy => _referencedBy;

        [NotNull]
        [SuppressMessage("ReSharper", "AssignNullToNotNullAttribute")]
        protected string FullName => _project.FullName;

        [Equals(StringComparison.OrdinalIgnoreCase)]
        [NotNull]
        public string UniqueName { get; }

        [NotNull]
        [SuppressMessage("ReSharper", "AssignNullToNotNullAttribute")]
        public string RelativeFolder => Path.GetDirectoryName(UniqueName);

        public bool IsTestProject => _projectTypeGuids.Contains("{3AC096D0-A1C2-E12C-1390-A8335801FDAB}");

        public bool IsVsProject => _vsProject != null;

        [CanBeNull]
        public string PrimaryOutputFileName { get; } 

        [NotNull, ItemNotNull]
        public IReadOnlyCollection<ProjectOutput> GetProjectOutput(bool deploySymbols, bool deployLocalizations, bool deployExternalLocalizations)
        {
            var binaryTargetDirectory = Path.GetDirectoryName(PrimaryOutputFileName) ?? string.Empty;

            return GetProjectOutput(this, deploySymbols, deployLocalizations, deployExternalLocalizations, binaryTargetDirectory);
        }

        [NotNull, ItemNotNull]
        private IReadOnlyCollection<ProjectOutput> GetProjectOutput([NotNull] Project rootProject, bool deploySymbols, bool deployLocalizations, bool deployExternalLocalizations, [NotNull] string binaryTargetDirectory)
        {
            var references = GetReferences();

            var projectOutput = GetBuildFiles(rootProject, deploySymbols, deployLocalizations, binaryTargetDirectory)
                .Concat(GetLocalFileReferences(rootProject, deployExternalLocalizations, references, binaryTargetDirectory)) // references must go to the same folder as the referencing component.
                .Concat(GetProjectReferences(references).SelectMany(reference => reference.SourceProject?.GetProjectOutput(rootProject, deploySymbols, deployLocalizations, deployExternalLocalizations, binaryTargetDirectory) ?? Enumerable.Empty<ProjectOutput>()));

            return projectOutput.ToArray();
        }

        [NotNull, ItemNotNull]
        private static IReadOnlyCollection<ProjectOutput> GetSecondTierReferences([NotNull, ItemNotNull] IEnumerable<VSLangProj.Reference> references, [NotNull] Project rootProject, bool deployExternalLocalizations, [NotNull] string targetDirectory)
        {
            // Try to resolve second-tier references for CopyLocal references
            return references
                .Where(reference => reference.CopyLocal)
                .Select(reference => reference.Path)
                .Where(File.Exists) // Reference can be a project reference, but project has not been built yet.
                                    // ReSharper disable once AssignNullToNotNullAttribute
                .SelectMany(file => GetReferencedAssemblyNames(file, deployExternalLocalizations))
                .Distinct()
                // ReSharper disable once AssignNullToNotNullAttribute
                .Select(file => new ProjectOutput(rootProject, file, targetDirectory))
                .ToArray();
        }

        [NotNull, ItemNotNull]
        [SuppressMessage("ReSharper", "AssignNullToNotNullAttribute")]
        [SuppressMessage("ReSharper", "PossibleNullReferenceException")]
        private static IReadOnlyCollection<string> GetReferencedAssemblyNames([NotNull] string assemblyFileName, bool deployExternalLocalizations)
        {
            try
            {
                var directory = Path.GetDirectoryName(assemblyFileName);

                var referencedAssemblyNames = AssemblyDefinition.ReadAssembly(assemblyFileName)
                    .MainModule
                    .AssemblyReferences
                    .Select(reference => reference.Name)
                    .Where(assemblyName => File.Exists(Path.Combine(directory, assemblyName + ".dll")))
                    .ToArray();

                var referencedAssemblyFileNames = referencedAssemblyNames
                    .Select(assemblyName => assemblyName + ".dll")
                    .ToArray();

                if (!deployExternalLocalizations)
                {
                    return referencedAssemblyFileNames;
                }

                var satteliteDlls = referencedAssemblyNames
                    .SelectMany(assemblyName => Directory.GetFiles(directory, assemblyName + ".resources.dll", SearchOption.AllDirectories))
                    .Select(file => file.Substring(directory.Length + 1));

                return referencedAssemblyFileNames
                    .Concat(satteliteDlls)
                    .ToArray();
            }
            catch
            {
                // assembly cannot be loaded
            }

            return new string[0];
        }

        [NotNull]
        public string Name
        {
            get
            {
                var name = _project.Name;
                Debug.Assert(name != null);
                return name;
            }
        }

        [NotNull]
        protected Solution Solution { get; }

        [NotNull, ItemNotNull]
        private IReadOnlyCollection<ProjectOutput> GetBuildFiles([NotNull] Project rootProject, bool deploySymbols, bool deployLocalizations, [NotNull] string binaryTargetDirectory)
        {
            var buildFileGroups = BuildFileGroups.Built | BuildFileGroups.ContentFiles;

            if (deployLocalizations)
                buildFileGroups |= BuildFileGroups.LocalizedResourceDlls;

            if (deploySymbols)
                buildFileGroups |= BuildFileGroups.Symbols;

            return GetBuildFiles(rootProject, buildFileGroups, binaryTargetDirectory);
        }

        [NotNull, ItemNotNull]
        private IReadOnlyCollection<ProjectOutput> GetBuildFiles([NotNull] Project rootProject, BuildFileGroups groups, [NotNull] string binaryTargetDirectory)
        {
            var groupNames = Enum.GetValues(typeof(BuildFileGroups)).OfType<BuildFileGroups>().Where(item => (groups & item) != 0);

            var outputGroups = _project.ConfigurationManager?.ActiveConfiguration?.OutputGroups;

            var selectedOutputGroups = groupNames
                .Select(groupName => outputGroups?.Item(groupName.ToString()))
                .Where(item => item != null);

            var buildFiles = selectedOutputGroups.SelectMany(item => GetProjectOutputForGroup(rootProject, item, binaryTargetDirectory));

            return buildFiles.ToArray();
        }

        [NotNull, ItemNotNull]
        protected IReadOnlyCollection<EnvDTE.ProjectItem> GetAllProjectItems()
        {
            return _project.EnumerateAllProjectItems().ToArray();
        }

        [NotNull, ItemNotNull]
        private IReadOnlyCollection<VSLangProj.Reference> GetReferences()
        {
            return GetVsProjectReferences() ?? GetMpfProjectReferences() ?? new VSLangProj.Reference[0];
        }

        protected void AddProjectReferences([NotNull] params Project[] projects)
        {
            var referencesCollection = ReferencesCollection;

            if (referencesCollection == null)
                return;

            var projectReferences = GetReferences()
                .Where(r => r.GetSourceProject() != null)
                .ToArray();

            var newProjects = projects
                // ReSharper disable once AssignNullToNotNullAttribute
                // ReSharper disable once SuspiciousTypeConversion.Global
                .Where(project => projectReferences.All(reference => !Equals(reference.GetSourceProject(), project)))
                .ToArray();

            foreach (var project in newProjects)
            {
                if (project == null)
                    continue;

                referencesCollection.AddProject(project._project);
            }
        }

        protected void RemoveProjectReferences([NotNull] params Project[] projects)
        {
            var references = GetReferences();

            var projectReferences = projects
                // ReSharper disable once AssignNullToNotNullAttribute
                // ReSharper disable once SuspiciousTypeConversion.Global
                .Select(project => references.FirstOrDefault(reference => Equals(reference.GetSourceProject(), project)))
                .ToArray();

            foreach (var reference in projectReferences)
            {
                reference?.Remove();
            }
        }

        [NotNull]
        protected EnvDTE.ProjectItem AddItemFromFile([NotNull] string fileName)
        {
            // ReSharper disable once AssignNullToNotNullAttribute
            // ReSharper disable once PossibleNullReferenceException
            return _project.ProjectItems.AddFromFile(fileName);
        }

        [CanBeNull]
        private VSLangProj.References ReferencesCollection
        {
            get
            {
                try
                {
                    if (_vsProject != null)
                        return _vsProject.References;

                    var projectItems = _project.ProjectItems;

                    return projectItems?
                        .OfType<EnvDTE.ProjectItem>()
                        .Select(p => p.Object)
                        .OfType<VSLangProj.References>()
                        .FirstOrDefault();
                }
                catch (ExternalException)
                {
                }

                return null;
            }
        }

        [CanBeNull]
        private IReadOnlyCollection<VSLangProj.Reference> GetMpfProjectReferences()
        {
            try
            {
                var projectItems = _project.ProjectItems;

                return projectItems?
                    .OfType<EnvDTE.ProjectItem>()
                    .Select(p => p.Object)
                    .OfType<VSLangProj.References>()
                    .Take(1)
                    // ReSharper disable once AssignNullToNotNullAttribute
                    .SelectMany(references => references.OfType<VSLangProj.Reference>())
                    .ToArray();
            }
            catch
            {
                return null;
            }
        }

        [CanBeNull]
        private IReadOnlyCollection<VSLangProj.Reference> GetVsProjectReferences()
        {
            try
            {
                return _vsProject?
                    .References?
                    .OfType<VSLangProj.Reference>()
                    .ToArray();
            }
            catch
            {
                return null;
            }
        }

        [NotNull, ItemNotNull]
        private static IReadOnlyCollection<ProjectOutput> GetProjectOutputForGroup([NotNull] Project project, [NotNull] EnvDTE.OutputGroup outputGroup, [NotNull] string binaryTargetDirectory)
        {
            var canonicalName = outputGroup.CanonicalName;

            if (!Enum.TryParse(canonicalName, out BuildFileGroups buildFileGroup))
                throw new InvalidOperationException("Unknown output group: " + canonicalName);

            var fileNames = outputGroup.GetFileNames();

            var projectOutputForGroup = fileNames.Select(fileName => new ProjectOutput(project, fileName, buildFileGroup, binaryTargetDirectory));

            return projectOutputForGroup.ToArray();
        }

        public override string ToString()
        {
            return UniqueName;
        }
    }

    internal static class ProjectExtension
    {
        [CanBeNull]
        public static EnvDTE.Project GetSourceProject([NotNull] this VSLangProj.Reference reference)
        {
            try
            {
                return reference.SourceProject;
            }
            catch (Exception)
            {
                return null;
            }
        }

        [NotNull, ItemNotNull]
        public static string[] GetFileNames([NotNull] this EnvDTE.OutputGroup outputGroup)
        {
            return InternalGetFileNames(outputGroup) ?? new string[0];
        }

        [CanBeNull]
        private static string[] InternalGetFileNames([NotNull] this EnvDTE.OutputGroup outputGroup)
        {
            try
            {
                return ((Array)outputGroup.FileNames)?.OfType<string>().ToArray();
            }
            catch
            {
                return null;
            }
        }
    }
}
