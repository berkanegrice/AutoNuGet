using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Loader;
using NuGet.Protocol.Core.Types;
namespace AutoNuGet
{
    public class PackageLoaderHelper
    {
        public string FolderName { get; init; }
        public NuGetConfiguration[] NuGetConfigurations { get; set;}

        #region [ Find correct *.dll respect to target framework ] 

        /// <summary>
        /// Get highest framework version
        /// </summary>
        /// <param name="frameworks"></param>
        /// <returns></returns>
        public static string GetTargetFramework(IEnumerable<string> frameworks)
        {
            var intHighestVersion = 0;
            string selectedFramework = null;
            var stringSeparators = new string[] { "netstandard"};

            foreach (var framework in frameworks)
            {
                var frameworkName = new DirectoryInfo(framework).Name;
                // ReSharper disable once InvertIf
                if (frameworkName.Contains("netstandard"))
                {
                    //parse name, version
                    var versions = frameworkName.Split(
                        stringSeparators, StringSplitOptions.RemoveEmptyEntries);
                    foreach (var version in versions)
                    {
                        var intVersion = int.Parse(OmitComma(version));
                        // ReSharper disable once InvertIf
                        if (intVersion > intHighestVersion)
                        {
                            intHighestVersion = intVersion;
                            selectedFramework = framework;
                        }
                    }
                }
            }
            return selectedFramework;
        }

        #endregion

        #region [ Comma Remover ]

        /// <summary>
        /// Function to omit comma "2'.'1"
        /// </summary>
        /// <param name="inpVersion"></param>
        /// <returns></returns>
        private static string OmitComma(string inpVersion)
        {
            var numbersWoDot  = inpVersion.Split(
                new string[] { "." }, StringSplitOptions.RemoveEmptyEntries);
            return numbersWoDot.Aggregate<string, string>(null, string.Concat);
        }

        #endregion

        #region [ Load Assemblies ] 

        public void LoadAssemblies(IEnumerable<SourcePackageDependencyInfo> assemblies)
        {
            var directory = Path.Combine(Directory.GetCurrentDirectory(), FolderName);
            foreach (var assembly in assemblies)
            {
                var frameworks = Directory.EnumerateDirectories(
                    Path.Combine(directory, string.Concat(assembly.Id, ".", assembly.Version), "lib"));

                var fullAssemblyPath = GetTargetFramework(frameworks);
                // Assume that package folder has a only one file(*.dll)
                fullAssemblyPath = new DirectoryInfo(fullAssemblyPath).GetFiles()[0].FullName;
                LoadAssembly(fullAssemblyPath);
            }
        }

        #endregion

        #region [ Load Assembly ]

        private static void LoadAssembly(string fullAssemblyPath)
        {
            try
            {
                if(new System.IO.FileInfo(fullAssemblyPath).Length > 0)
                    AssemblyLoadContext.Default.LoadFromAssemblyPath(fullAssemblyPath);
            }
            catch (Exception)
            {
                Console.WriteLine(fullAssemblyPath + "cannot loaded");
            }
        }

        #endregion
    }
}
