using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;

namespace ProjectJsonSideBySide
{
    public static class Program
    {
        private static readonly XNamespace MsBuildNamespace = @"http://schemas.microsoft.com/developer/msbuild/2003";

        public static void Main(string[] args)
        {
            MainAsync(args).GetAwaiter().GetResult();
        }

        private static async Task MainAsync(IReadOnlyList<string> args)
        {
            var solutionPath = args[0];

            var workspace = MSBuildWorkspace.Create();

            var solution = await workspace.OpenSolutionAsync(solutionPath);

            var contexts = new Dictionary<ProjectId, ProjectContext>();

            foreach (var projectId in solution.ProjectIds)
            {
                var project = solution.GetProject(projectId);

                var projectPath = project.FilePath;

                var projectFolder = Path.GetDirectoryName(projectPath);

                if (!projectFolder.Contains("samples"))
                {
                    continue; // Skip samples.
                }

                Directory.SetCurrentDirectory(projectFolder);

                var projectFileName = Path.GetFileName(projectPath);

                var projectFolderName = Path.GetFileName(projectFolder);

                var newProjectFolder = Path.GetFullPath(Path.Combine("..", projectFolderName.Replace(".MSBuild", string.Empty)));

                var newProjectFolderName = Path.GetFileName(newProjectFolder);

                var newProjectPath = Path.Combine(newProjectFolder, projectFileName);

                var context = new ProjectContext
                {
                    Path = newProjectPath,
                    FolderPath = newProjectFolder,
                    FolderName = newProjectFolderName
                };

                contexts.Add(projectId, context);

                MoveProject(projectPath, newProjectPath, projectFolder, newProjectFolder);

                AdjustSolution(solution.FilePath, projectPath, newProjectPath);
            }

            Console.WriteLine("Moved projects. Press a key to continue...");
            Console.ReadLine();

            foreach (var keyValue in contexts)
            {
                var context = keyValue.Value;

                Directory.SetCurrentDirectory(context.FolderPath);

                var fullProjectPath = Path.GetFullPath(context.Path);

                AdjustProject(fullProjectPath, context.FolderName);
            }
        }

        private class ProjectContext
        {
            public string Path { get; set; }

            public string FolderPath { get; set; }

            public string FolderName { get; set; }
        }

        private static void AdjustSolution(string solutionPath, string projectPath, string newProjectPath)
        {
            var relativeProjectPath = GetRelativePath(solutionPath, projectPath);
            var newRelativeProjectPath = GetRelativePath(solutionPath, newProjectPath);

            var solutionText = File.ReadAllText(solutionPath);

            var newSolutionText = solutionText.Replace(relativeProjectPath, newRelativeProjectPath);

            File.WriteAllText(solutionPath, newSolutionText);
        }

        private static void MoveProject(string projectPath, string newProjectPath, string projectFolder, string newProjectFolder)
        {
            if (!Directory.Exists(newProjectFolder))
            {
                Directory.CreateDirectory(newProjectFolder);
            }

            File.Move(projectPath, newProjectPath);

            var packagesConfigPath = Path.Combine(projectFolder, "packages.config");

            if (File.Exists(packagesConfigPath))
            {
                var newPackagesConfigPath = Path.Combine(newProjectFolder, "packages.config");

                File.Move(packagesConfigPath, newPackagesConfigPath);
            }
        }

        private static void AdjustProject(string projectPath, string projectFolderName)
        {
            var document = LoadXmlDocument(projectPath);

            AdjustDocumentPaths(document, projectPath, projectFolderName);

            RemoveLegacyElements(document);

            RemoveEmptyElements(document);

            document.Save(projectPath);
        }

        private static void AdjustDocumentPaths(XContainer document, string projectPath, string projectFolderName)
        {
            //AdjustOutputPaths(document, projectPath, projectFolderName);

            AdjustElementPaths(document, projectPath, projectFolderName, "EmbeddedResource");
            AdjustElementPaths(document, projectPath, projectFolderName, "Compile");
            AdjustElementPaths(document, projectPath, projectFolderName, "Content");
            AdjustElementPaths(document, projectPath, projectFolderName, "None");

            AdjustReferencePaths(document, projectPath);
        }

        private static void AdjustOutputPaths(XContainer document, string projectPath, string projectFolderName)
        {
            var elements = document.Descendants(MsBuildNamespace + "OutputPath").ToArray();

            foreach (var element in elements)
            {
                var newPath = Path.Combine("..", projectFolderName, element.Value);

                var fullPath = Path.GetFullPath(newPath);

                var relativePath = GetRelativePath(projectPath, fullPath);

                element.SetValue(relativePath);
            }
        }

        private static void AdjustElementPaths(XContainer document, string projectPath, string projectFolderName, string elementName)
        {
            var elements = document.Descendants(MsBuildNamespace + elementName).ToArray();

            foreach (var element in elements)
            {
                var includeAttribute = element.Attribute("Include");

                var value = includeAttribute.Value;

                if (value.EndsWith("packages.config", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var newPath = Path.Combine("..", projectFolderName, value);

                var fullPath = Path.GetFullPath(newPath);

                var relativePath = GetRelativePath(projectPath, fullPath);

                includeAttribute.SetValue(relativePath);

                if (value.Contains(projectFolderName))
                {
                    element.Descendants(MsBuildNamespace + "Link").Remove();
                }
            }
        }

        private static void AdjustReferencePaths(XContainer document, string projectPath)
        {
            var elements = document.Descendants(MsBuildNamespace + "ProjectReference").ToArray();

            foreach (var element in elements)
            {
                var includeAttribute = element.Attribute("Include");

                var projectFileName = Path.GetFileName(includeAttribute.Value);

                var projectName = Path.GetFileNameWithoutExtension(projectFileName);

                var oldProjectName = $"{projectName}.MSBuild";

                var newPath = includeAttribute.Value.Replace(oldProjectName, projectName);

                var fullPath = Path.GetFullPath(newPath);

                var relativePath = GetRelativePath(projectPath, fullPath);

                includeAttribute.SetValue(relativePath);
            }
        }

        private static void RemoveLegacyElements(XContainer document)
        {
            document.Descendants(MsBuildNamespace + "BootstrapperPackage").ToArray().Remove();

            var elements = document.Descendants(MsBuildNamespace + "Reference").ToArray();

            var references = new[]
            {
                "System.Web.Entity",
                "System.Web.Services",
                "System.Web.Extensions",
                "System.Web.DynamicData",
                "System.EnterpriseServices",
                "System.Web.ApplicationServices",
                "System.Data.DataSetExtensions"
            };

            foreach (var element in elements)
            {
                var includeAttribute = element.Attribute("Include");

                if (string.IsNullOrEmpty(includeAttribute?.Value))
                {
                    element.Remove();
                    continue;
                }

                if (references.Any(@ref => includeAttribute.Value.Contains(@ref)))
                {
                    element.Remove();
                }
            }
        }

        private static void RemoveEmptyElements(XContainer document)
        {
            // Collapse start and end tags
            document.Descendants().Where(element => string.IsNullOrWhiteSpace(element.Value) && !element.HasElements).ToList().ForEach(x => x.RemoveNodes());

            // Remove empty tags
            document.Descendants().Where(element => element.IsEmpty()).Remove();
        }

        private static bool IsEmpty(this XElement element)
        {
            return element.HasNoValue() && !element.HasAttributes && !element.HasElements;
        }

        private static bool HasNoValue(this XElement element)
        {
            return element.IsEmpty || string.IsNullOrWhiteSpace(element.Value);
        }

        private static XDocument LoadXmlDocument(string path)
        {
            using (var stream = File.OpenRead(path))
            {
                return XDocument.Load(stream);
            }
        }

        private static string GetRelativePath(string fromPath, string toPath)
        {
            if (string.IsNullOrEmpty(fromPath))
            {
                throw new ArgumentNullException(nameof(fromPath));
            }

            if (string.IsNullOrEmpty(toPath))
            {
                throw new ArgumentNullException(nameof(toPath));
            }

            var fromUri = new Uri(fromPath);
            var toUri = new Uri(toPath);

            if (!fromUri.Scheme.Equals(toUri.Scheme, StringComparison.OrdinalIgnoreCase))
            {
                return toPath;
            }

            var relativeUri = fromUri.MakeRelativeUri(toUri);
            var relativePath = Uri.UnescapeDataString(relativeUri.ToString());

            if (toUri.Scheme.Equals("file", StringComparison.OrdinalIgnoreCase))
            {
                relativePath = relativePath.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
            }

            return relativePath;
        }
    }
}
