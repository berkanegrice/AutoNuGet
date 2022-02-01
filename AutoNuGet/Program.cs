using NuGet.Protocol.Core.Types;
using NuGet.Versioning;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;

namespace AutoNuGet
{
    internal static class Program
    {
        #region [ Print types to check assemblies are loaded correctly ]

        private static void PrintTypes(Assembly assembly)
        {
            foreach (TypeInfo type in assembly.DefinedTypes)
            {
                Console.WriteLine(type.Name);
                foreach (PropertyInfo property in type.DeclaredProperties)
                {
                    string attributes = string.Join(
                        ", ",
                        property.CustomAttributes.Select(a => a.AttributeType.Name));

                    if (!string.IsNullOrEmpty(attributes))
                    {
                        Console.WriteLine("    [{0}]", attributes);
                    }
                    Console.WriteLine("    {0} {1}", property.PropertyType.Name, property.Name);
                }
            }
        }

        #endregion

        #region [ Path to downloaded main assembly(*.dll) ]

        public static string GetDownloadedAssemblyPath(NuGetConfiguration NuGetConf,
            string nugetRepository)
        {
            string pathToAvailableFrameworksDir = Path.Combine(Directory.GetCurrentDirectory(), nugetRepository,
                string.Concat(NuGetConf.Package, ".", NuGetConf.Version), "lib");

            IEnumerable<string> availableFrameworks = Directory.EnumerateDirectories(pathToAvailableFrameworksDir);
            string framework = PackageLoaderHelper.GetTargetFramework(availableFrameworks);
            
            return Path.Combine(framework, string.Concat(NuGetConf.Package, ".dll"));
        }

        #endregion

        static void Main(string[] args)
        {
            // Create a instances of own packageManager
            var packageHelper = new PackageDownloaderHelper();

            Console.Write("Enter a folder name to nuget package repository : ");
            var folderName = Console.ReadLine(); 

            Console.Write("Enter a NuGet package name : ");
            var packageName = Console.ReadLine();

            // ReSharper disable once SuggestVarOrType_Elsewhere
            Task<IEnumerable<NuGetVersion>> availableVersions = 
                PackageDownloaderHelper.ListAvailablePackages(packageName);

            foreach (var version in availableVersions.Result)
            {
                Console.WriteLine(version);
            }
            Console.Write("Enter a NuGet package version : ");
            var packageVersion = Console.ReadLine();

            var packageToInstall = new NuGetConfiguration[]
            {
                new NuGetConfiguration
                {
                    Package = packageName,
                    PreRelease = true,
                    Version = packageVersion
                }
            };

            packageHelper = new PackageDownloaderHelper(packageToInstall, folderName);
            // ReSharper disable once SuggestVarOrType_Elsewhere
            Task<IEnumerable<SourcePackageDependencyInfo>> task = packageHelper.DownloadPackage();
            
            task.Wait();
            if (task.IsCompletedSuccessfully)
            {
                Console.WriteLine("\nPackage downloaded successfully");
                Console.WriteLine("Package Loading has started..");

                var packageLoaderHelper = new PackageLoaderHelper()
                {
                    FolderName = folderName
                };

                packageLoaderHelper.LoadAssemblies(task.Result);
                Console.WriteLine("\nPackage Loading successfully has finished..");

                //Generate a downloaded assembly path.
                foreach (var package in packageToInstall)
                {
                    //Print all types on that loaded assembly
                    var assemblyPath = GetDownloadedAssemblyPath(package, folderName);
                    PrintTypes(Assembly.LoadFrom(assemblyPath));
                }
            }
            else
            {
                Console.WriteLine("An error has occurred in the NuGet package downloader.");
            }
        }
    }
}
