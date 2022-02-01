using System;
using System.IO;
using System.Linq;
using System.Threading;
using NuGet.Configuration;
using System.Threading.Tasks;
using System.Collections.Generic;
using NuGet.Common;
using NuGet.Protocol;
using NuGet.Resolver;
using NuGet.Packaging;
using NuGet.Versioning;
using NuGet.Frameworks;
using NuGet.Packaging.Core;
using NuGet.Packaging.Signing;
using NuGet.Protocol.Core.Types;
using Microsoft.Extensions.DependencyModel;

namespace AutoNuGet
{
    internal class PackageDownloaderHelper
    {
        private NuGetConfiguration[] Extensions { get; set; }
        private string RepositoryName { get; set; }
        public PackageDownloaderHelper() { }
        public PackageDownloaderHelper(NuGetConfiguration[] extensions, string repositoryName)
        {
            Extensions = extensions;
            RepositoryName = repositoryName;
        }

        #region [ LoadPackage ] 
        public async Task<IEnumerable<SourcePackageDependencyInfo>> DownloadPackage()
        {
            var sourceProvider = new PackageSourceProvider(NullSettings.Instance, new[]
            {
                new PackageSource("https://api.nuget.org/v3/index.json"),
            });

            using var sourceCacheContext = new SourceCacheContext();
            var sourceRepositoryProvider = new SourceRepositoryProvider(sourceProvider, Repository.Provider.GetCoreV3());
            var repositories = sourceRepositoryProvider.GetRepositories();
            var logger = new NullLogger();
            var targetFramework = NuGetFramework.ParseFolder("netcoreapp5.0");
            var allPackagedWithDependencies = new HashSet<SourcePackageDependencyInfo>();

            foreach (var ext in Extensions)
            {
                // ReSharper disable once PossibleMultipleEnumeration
                var packageIdentity = await GetPackageIdentity(ext, sourceCacheContext, logger, repositories, CancellationToken.None);

                if (packageIdentity is null)
                {
                    throw new InvalidOperationException($"Cannot find package {ext.Package}.");
                }

                await GetPackageDependencies(packageIdentity, sourceCacheContext, targetFramework, 
                    // ReSharper disable once PossibleMultipleEnumeration
                    logger, repositories, DependencyContext.Default, allPackagedWithDependencies, 
                    CancellationToken.None);
            }

            var packagesToInstall = GetPackagesToInstall(sourceRepositoryProvider, logger, Extensions, allPackagedWithDependencies);
            var packageDirectory = Path.Combine(Environment.CurrentDirectory, RepositoryName);
            var nugetSettings = Settings.LoadDefaultSettings(packageDirectory);

            //inner process(works for each depended package, that's why called DownloadPackage's')
            // ReSharper disable once PossibleMultipleEnumeration
            await DownloadPackages(sourceCacheContext, logger, packagesToInstall, packageDirectory, nugetSettings, CancellationToken.None);

            // ReSharper disable once PossibleMultipleEnumeration
            return packagesToInstall;
        }

        #endregion

        #region [ Get Package Dependencies ]
        private static async Task GetPackageDependencies(PackageIdentity package, SourceCacheContext cacheContext, NuGetFramework framework,
                                          ILogger logger, IEnumerable<SourceRepository> repositories, DependencyContext hostDependencies,
                                          ISet<SourcePackageDependencyInfo> availablePackages, CancellationToken cancelToken)
        {
            if (availablePackages.Contains(package))
            {
                return;
            }

            // ReSharper disable once PossibleMultipleEnumeration
            foreach (var sourceRepository in repositories)
            {
                // Get the dependency info for the package.
                var dependencyInfoResource = await sourceRepository.GetResourceAsync<DependencyInfoResource>();
                var dependencyInfo = await dependencyInfoResource.ResolvePackage(
                    package,
                    framework,
                    cacheContext,
                    logger,
                    cancelToken);

                // Corner-case to eliminate when dependencyInfo is null.
                if (dependencyInfo == null)
                {
                    continue;
                }

                // Filter the dependency info.
                // Don't load in any dependencies that are provided by the host.
                var actualSourceDep = new SourcePackageDependencyInfo(
                    dependencyInfo.Id,
                    dependencyInfo.Version,
                    dependencyInfo.Dependencies.Where(dep => !DependencySuppliedByHost(hostDependencies, dep)),
                    dependencyInfo.Listed,
                    dependencyInfo.Source);

                availablePackages.Add(actualSourceDep);

                // Recurse through each package.
                foreach (var dependency in actualSourceDep.Dependencies)
                {
                    await GetPackageDependencies(
                        new PackageIdentity(
                        dependency.Id,
                        dependency.VersionRange.MinVersion),
                        cacheContext,
                        framework,
                        logger,
                        // ReSharper disable once PossibleMultipleEnumeration
                        repositories,
                        hostDependencies,
                        availablePackages,
                        cancelToken);
                }
                break;
            }
        }

        #endregion

        #region [ DependencySuppliedByHost ]

        private static bool DependencySuppliedByHost(DependencyContext hostDependencies, PackageDependency dep)
        {
            if (RuntimeProvidedPackages.IsPackageProvidedByRuntime(dep.Id))
            {
                return true;
            }

            // See if a runtime library with the same ID as the package is available in the host's runtime libraries.
            var runtimeLib = hostDependencies.RuntimeLibraries.FirstOrDefault(r => r.Name == dep.Id);

            if (runtimeLib is not { }) return false;
            // What version of the library is the host using?
            var parsedLibVersion = NuGetVersion.Parse(runtimeLib.Version);

            return parsedLibVersion.IsPrerelease || dep.VersionRange.Satisfies(parsedLibVersion);
        }

        #endregion

        #region [ List all available version of Package ]

        public static async Task<IEnumerable<NuGetVersion>> ListAvailablePackages(string packageName)
        {
            var logger = NullLogger.Instance;
            var cancelllationToken = CancellationToken.None;

            var cache = new SourceCacheContext();
            var repository = Repository.Factory.GetCoreV3("https://api.nuget.org/v3/index.json");
            var resource = await repository.GetResourceAsync<FindPackageByIdResource>(cancelllationToken);

            var versions = await resource.GetAllVersionsAsync(
                packageName,
                cache,
                logger,
                cancelllationToken);

            return versions;
        }

        #endregion

        #region [ Get Packages ] 
        private static IEnumerable<SourcePackageDependencyInfo> GetPackagesToInstall(SourceRepositoryProvider sourceRepositoryProvider,
                                                                      ILogger logger, IEnumerable<NuGetConfiguration> extensions,
                                                                      HashSet<SourcePackageDependencyInfo> allPackages)
        {
            // Create a package resolver context (this is used to help figure out which actual package versions to install).
            var resolverContext = new PackageResolverContext(
                   DependencyBehavior.Lowest,
                   extensions.Select(x => x.Package),
                   Enumerable.Empty<string>(),
                   Enumerable.Empty<PackageReference>(),
                   Enumerable.Empty<PackageIdentity>(),
                   allPackages,
                   sourceRepositoryProvider.GetRepositories().Select(s => s.PackageSource),
                   logger);

            var resolver = new PackageResolver();
            var packagesToInstall = resolver.Resolve(resolverContext, CancellationToken.None)
                                            .Select(p => allPackages.Single(x => PackageIdentityComparer.Default.Equals(x, p)));
            return packagesToInstall;
        }

        #endregion

        #region [ Get PackageIdentity ] 

        private static async Task<PackageIdentity> GetPackageIdentity(
          NuGetConfiguration extConfig, SourceCacheContext cache, ILogger nugetLogger,
          IEnumerable<SourceRepository> repositories, CancellationToken cancelToken)
        {
            foreach (var sourceRepository in repositories)
            {
                var findPackageResource = await sourceRepository.GetResourceAsync<FindPackageByIdResource>(cancelToken);
                var allVersions = await findPackageResource.GetAllVersionsAsync(extConfig.Package, cache, nugetLogger, cancelToken);

                NuGetVersion selected;

                if (extConfig.Version != null)
                {
                    if (!VersionRange.TryParse(extConfig.Version, out var range))
                    {
                        throw new InvalidOperationException("Invalid version range provided.");
                    }
                    var bestVersion = range.FindBestMatch(allVersions.Where(v => extConfig.PreRelease || !v.IsPrerelease));
                    selected = bestVersion;
                }
                else
                {
                    // If no version; choose the latest
                    selected = allVersions.LastOrDefault(v => v.IsPrerelease == extConfig.PreRelease);
                }

                if (selected is not null)
                {
                    return new PackageIdentity(extConfig.Package, selected);
                }
            }
            return null;
        }

        #endregion

        #region [ Download Packages ]
        private static async Task DownloadPackages(SourceCacheContext sourceCacheContext, ILogger logger,
                                   IEnumerable<SourcePackageDependencyInfo> packagesToInstall, string rootPackagesDirectory,
                                   ISettings nugetSettings, CancellationToken cancellationToken)
        {
            var packagePathResolver = new PackagePathResolver(rootPackagesDirectory, true);
            var packageExtractionContext = new PackageExtractionContext(
                PackageSaveMode.Defaultv3,
                XmlDocFileSaveMode.Skip,
                ClientPolicyContext.GetClientPolicy(nugetSettings, logger),
                logger);

            foreach (var package in packagesToInstall)
            {
                var downloadResource = await package.Source.GetResourceAsync<DownloadResource>(cancellationToken);

                // Download the package (may already installed on cache)
                var downloadResult = await downloadResource.GetDownloadResourceResultAsync(
                    package,
                    new PackageDownloadContext(sourceCacheContext),
                    SettingsUtility.GetGlobalPackagesFolder(nugetSettings),
                    logger,
                    cancellationToken);

                // Extract the package into the target directory.
                await PackageExtractor.ExtractPackageAsync(
                    downloadResult.PackageSource,
                    downloadResult.PackageStream,
                    packagePathResolver,
                    packageExtractionContext,
                    cancellationToken);
            }
        }

        #endregion
    }
}
