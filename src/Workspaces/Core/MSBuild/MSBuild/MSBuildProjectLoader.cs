﻿// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

using System;
using System.Collections.Immutable;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.MSBuild.Build;
using Roslyn.Utilities;
using MSB = Microsoft.Build;

namespace Microsoft.CodeAnalysis.MSBuild
{
    /// <summary>
    /// An API for loading msbuild project files.
    /// </summary>
    public partial class MSBuildProjectLoader
    {
        // the workspace that the projects and solutions are intended to be loaded into.
        private readonly Workspace _workspace;

        private readonly DiagnosticReporter _diagnosticReporter;
        private readonly PathResolver _pathResolver;
        private readonly ProjectFileLoaderRegistry _projectFileLoaderRegistry;

        // used to protect access to the following mutable state
        private readonly NonReentrantLock _dataGuard = new NonReentrantLock();
        private ImmutableDictionary<string, string> _properties;

        internal readonly ProjectBuildManager BuildManager;

        internal MSBuildProjectLoader(
            Workspace workspace,
            DiagnosticReporter diagnosticReporter,
            ProjectFileLoaderRegistry projectFileLoaderRegistry,
            ImmutableDictionary<string, string> properties)
        {
            _workspace = workspace;
            _diagnosticReporter = diagnosticReporter ?? new DiagnosticReporter(workspace);
            _pathResolver = new PathResolver(_diagnosticReporter);
            _projectFileLoaderRegistry = projectFileLoaderRegistry ?? new ProjectFileLoaderRegistry(workspace, _diagnosticReporter);

            _properties = ImmutableDictionary.Create<string, string>(StringComparer.OrdinalIgnoreCase);

            if (properties != null)
            {
                _properties = _properties.AddRange(properties);
            }

            BuildManager = new ProjectBuildManager();
        }

        /// <summary>
        /// Create a new instance of an <see cref="MSBuildProjectLoader"/>.
        /// </summary>
        public MSBuildProjectLoader(Workspace workspace, ImmutableDictionary<string, string> properties = null)
            : this(workspace, diagnosticReporter: null, projectFileLoaderRegistry: null, properties)
        {
        }

        /// <summary>
        /// The MSBuild properties used when interpreting project files.
        /// These are the same properties that are passed to msbuild via the /property:&lt;n&gt;=&lt;v&gt; command line argument.
        /// </summary>
        public ImmutableDictionary<string, string> Properties => _properties;

        /// <summary>
        /// Determines if metadata from existing output assemblies is loaded instead of opening referenced projects.
        /// If the referenced project is already opened, the metadata will not be loaded.
        /// If the metadata assembly cannot be found the referenced project will be opened instead.
        /// </summary>
        public bool LoadMetadataForReferencedProjects { get; set; } = false;

        /// <summary>
        /// Determines if unrecognized projects are skipped when solutions or projects are opened.
        /// 
        /// A project is unrecognized if it either has 
        ///   a) an invalid file path, 
        ///   b) a non-existent project file,
        ///   c) has an unrecognized file extension or 
        ///   d) a file extension associated with an unsupported language.
        /// 
        /// If unrecognized projects cannot be skipped a corresponding exception is thrown.
        /// </summary>
        public bool SkipUnrecognizedProjects { get; set; } = true;

        /// <summary>
        /// Associates a project file extension with a language name.
        /// </summary>
        public void AssociateFileExtensionWithLanguage(string projectFileExtension, string language)
        {
            if (projectFileExtension == null)
            {
                throw new ArgumentNullException(nameof(projectFileExtension));
            }

            if (language == null)
            {
                throw new ArgumentNullException(nameof(language));
            }

            _projectFileLoaderRegistry.AssociateFileExtensionWithLanguage(projectFileExtension, language);
        }

        private void SetSolutionProperties(string solutionFilePath)
        {
            const string SolutionDirProperty = "SolutionDir";

            // When MSBuild is building an individual project, it doesn't define $(SolutionDir).
            // However when building an .sln file, or when working inside Visual Studio,
            // $(SolutionDir) is defined to be the directory where the .sln file is located.
            // Some projects out there rely on $(SolutionDir) being set (although the best practice is to
            // use MSBuildProjectDirectory which is always defined).
            if (!string.IsNullOrEmpty(solutionFilePath))
            {
                var solutionDirectory = Path.GetDirectoryName(solutionFilePath);
                if (!solutionDirectory.EndsWith(@"\", StringComparison.Ordinal))
                {
                    solutionDirectory += @"\";
                }

                if (Directory.Exists(solutionDirectory))
                {
                    _properties = _properties.SetItem(SolutionDirProperty, solutionDirectory);
                }
            }
        }

        private DiagnosticReportingMode GetReportingModeForUnrecognizedProjects()
            => this.SkipUnrecognizedProjects
                ? DiagnosticReportingMode.Log
                : DiagnosticReportingMode.Throw;

        /// <summary>
        /// Loads the <see cref="SolutionInfo"/> for the specified solution file, including all projects referenced by the solution file and 
        /// all the projects referenced by the project files.
        /// </summary>
        public async Task<SolutionInfo> LoadSolutionInfoAsync(
            string solutionFilePath,
            IProgress<ProjectLoadProgress> progress = null,
            CancellationToken cancellationToken = default)
        {
            if (solutionFilePath == null)
            {
                throw new ArgumentNullException(nameof(solutionFilePath));
            }

            if (!_pathResolver.TryGetAbsoluteSolutionPath(solutionFilePath, baseDirectory: Directory.GetCurrentDirectory(), DiagnosticReportingMode.Throw, out var absoluteSolutionPath))
            {
                // TryGetAbsoluteSolutionPath should throw before we get here.
                return null;
            }

            using (_dataGuard.DisposableWait(cancellationToken))
            {
                this.SetSolutionProperties(absoluteSolutionPath);
            }

            var solutionFile = MSB.Construction.SolutionFile.Parse(absoluteSolutionPath);

            var reportingMode = GetReportingModeForUnrecognizedProjects();

            var requestedProjectOptions = new DiagnosticReportingOptions(
                onPathFailure: reportingMode,
                onLoaderFailure: reportingMode);

            var discoveredProjectOptions = new DiagnosticReportingOptions(
                onPathFailure: reportingMode,
                onLoaderFailure: reportingMode);

            var projectPaths = ImmutableArray.CreateBuilder<string>();

            // load all the projects
            foreach (var project in solutionFile.ProjectsInOrder)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (project.ProjectType != MSB.Construction.SolutionProjectType.SolutionFolder)
                {
                    projectPaths.Add(project.RelativePath);
                }
            }

            var worker = new Worker(
                _workspace,
                _diagnosticReporter,
                _pathResolver,
                _projectFileLoaderRegistry,
                BuildManager,
                projectPaths.ToImmutable(),
                baseDirectory: Path.GetDirectoryName(absoluteSolutionPath),
                _properties,
                projectMap: null,
                progress,
                requestedProjectOptions,
                discoveredProjectOptions,
                preferMetadataForReferencedProjects: false);

            var projects = await worker.LoadAsync(cancellationToken).ConfigureAwait(false);

            // construct workspace from loaded project infos
            return SolutionInfo.Create(
                SolutionId.CreateNewId(debugName: absoluteSolutionPath),
                version: default,
                absoluteSolutionPath,
                projects);
        }

        /// <summary>
        /// Loads the <see cref="ProjectInfo"/> from the specified project file and all referenced projects.
        /// The first <see cref="ProjectInfo"/> in the result corresponds to the specified project file.
        /// </summary>
        public async Task<ImmutableArray<ProjectInfo>> LoadProjectInfoAsync(
            string projectFilePath,
            ProjectMap projectMap = null,
            IProgress<ProjectLoadProgress> progress = null,
            CancellationToken cancellationToken = default)
        {
            if (projectFilePath == null)
            {
                throw new ArgumentNullException(nameof(projectFilePath));
            }

            var requestedProjectOptions = DiagnosticReportingOptions.ThrowForAll;

            var reportingMode = GetReportingModeForUnrecognizedProjects();

            var discoveredProjectOptions = new DiagnosticReportingOptions(
                onPathFailure: reportingMode,
                onLoaderFailure: reportingMode);

            var worker = new Worker(
                _workspace,
                _diagnosticReporter,
                _pathResolver,
                _projectFileLoaderRegistry,
                BuildManager,
                requestedProjectPaths: ImmutableArray.Create(projectFilePath),
                baseDirectory: Directory.GetCurrentDirectory(),
                globalProperties: _properties,
                projectMap,
                progress,
                requestedProjectOptions,
                discoveredProjectOptions,
                this.LoadMetadataForReferencedProjects);

            return await worker.LoadAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}
