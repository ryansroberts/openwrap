﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using OpenFileSystem.IO;
using OpenWrap.Dependencies;
using OpenWrap.Repositories;

namespace OpenWrap.Commands.Wrap
{
    [Command(Verb = "add", Noun = "wrap")]
    public class AddWrapCommand : WrapCommand
    {
        [CommandInput(IsRequired = true, Position = 0)]
        public string Name { get; set; }

        [CommandInput]
        public bool NoDescriptorUpdate { get; set; }

        [CommandInput]
        public bool ProjectOnly { get; set; }

        [CommandInput]
        public bool SystemOnly { get; set; }

        [CommandInput]
        public bool Content { get; set; }

        [CommandInput(Position = 1)]
        public string Version { get; set; }

        [CommandInput]
        public string MinVersion { get; set; }

        [CommandInput]
        public string MaxVersion { get; set; }

        [CommandInput]
        public bool Anchored { get; set; }


        bool ShouldUpdateDescriptor
        {
            get
            {
                return NoDescriptorUpdate == false &&
                       Environment.Descriptor != null &&
                       SystemOnly == false;
            }
        }

        public override IEnumerable<ICommandOutput> Execute()
        {
            return Either(ValidateInputs()).Or(ExecuteCore());
        }

        IEnumerable<ICommandOutput> ValidateInputs()
        {
            var gotVersion = Version != null;
            var gotMinVersion = MinVersion != null;
            var gotMaxVersion = MaxVersion != null;
            var numberOfVersionInputTypes = (new[] { gotVersion, (gotMinVersion || gotMaxVersion) }).Count(v => v);

            if (numberOfVersionInputTypes > 1)
            {
                yield return new Error("Arguments for 'version' and 'version boundaries' cannot be combined.");
                yield break;
            }

            if (gotVersion && Version.ToVersion() == null)
            {
                yield return new Error("Could not parse version: " + Version);
                yield break;
            }

            if (gotMinVersion && MinVersion.ToVersion() == null)
            {
                yield return new Error("Could not parse minversion: " + MinVersion);
                yield break;
            }

            if (gotMaxVersion && MaxVersion.ToVersion() == null)
            {
                yield return new Error("Could not parse maxversion: " + MaxVersion);
                yield break;
            }
        }

        IEnumerable<ICommandOutput> ExecuteCore()
        {
            if (Name.EndsWith(".wrap", StringComparison.OrdinalIgnoreCase))
            {
                var desc = WrapFileToPackageDescriptor();
                yield return desc;
            }

            yield return VerifyWrapFile();
            yield return VeryfyWrapRepository();

            var packageDescriptor = DescriptorFromCommand();

            if (ShouldUpdateDescriptor)
                yield return UpdateDescriptor();

            var sourceRepositories = Environment.RepositoriesForRead();
            var resolvedDependencies = ResolveDependencies(packageDescriptor, sourceRepositories);

            if (!resolvedDependencies.IsSuccess)
            {
                yield return new DependencyResolutionFailedResult(string.Format("Could not find a package for dependency '{0}'.", Name), resolvedDependencies);
                yield break;
            }
            foreach (var m in resolvedDependencies.GacConflicts(Environment.ExecutionEnvironment))
                yield return m;

            var repositoriesToCopyTo = Environment.RemoteRepositories.Concat(new[]
            {
                Environment.CurrentDirectoryRepository,
                ProjectOnly ? null : Environment.SystemRepository,
                SystemOnly ? null : Environment.ProjectRepository
            }).NotNull();

            foreach (var msg in PackageManager.CopyPackagesToRepositories(resolvedDependencies, repositoriesToCopyTo.NotNull()))
                yield return msg;

            foreach (var m in PackageManager.VerifyPackageCache(Environment, Environment.Descriptor)) yield return m;
        }

        ICommandOutput UpdateDescriptor()
        {
            ICommandOutput outputMessage;
            var dependencyWithSameName = Environment.Descriptor.Dependencies.Where(x => x.Name.EqualsNoCase(Name)).ToList();
            if (dependencyWithSameName.Count > 0)
            {
                outputMessage = new GenericMessage("Dependency already declared in descriptor, updating.");
                foreach(var i in dependencyWithSameName)
                    Environment.Descriptor.Dependencies.Remove(i);
            }
            else
            {
                outputMessage = new GenericMessage("Dependency added to descriptor.");
            }
            Environment.Descriptor.Dependencies.Add(new PackageDependency
            {
                    Anchored = Anchored, 
                    Name = Name, 
                    VersionVertices = VersionVertices(),
                    ContentOnly = Content
            });
            using(var descriptor = Environment.DescriptorFile.OpenWrite())
                new PackageDescriptorReaderWriter().Write(Environment.Descriptor, descriptor);
            return outputMessage;
        }

        ICommandOutput WrapFileToPackageDescriptor()
        {
            if (Path.GetExtension(Name).Equals(".wrap", StringComparison.OrdinalIgnoreCase) && Environment.CurrentDirectory.GetFile(Path.GetFileName(Name)).Exists)
            {
                var originalName = Name;
                Name = PackageNameUtility.GetName(Path.GetFileNameWithoutExtension(Name));
                Version = "= " + PackageNameUtility.GetVersion(Path.GetFileNameWithoutExtension(originalName));
                return
                        new GenericMessage(
                                string.Format("The requested package contained '.wrap' in the name. Assuming you pointed to the file in the current directory and meant a package named '{0}' with version qualifier '{1}'.",
                                              Name,
                                              Version))
                        {
                            Type = CommandResultType.Warning
                        };
            }
            if (File.Exists(Name))
            {
                return new Error("You have given a path to a .wrap file that is not in the current directory but exists on disk. This is not currently supported. Go to the directory, and re-issue the command.");
            }
            return null;
        }

        PackageDescriptor DescriptorFromCommand()
        {
            return new PackageDescriptor
            {
                Dependencies =
                    {
                        new PackageDependency
                        {
                            Name = Name,
                            Anchored = Anchored,
                            ContentOnly = Content,
                            VersionVertices = VersionVertices()
                        }
                    }
            };
        }

        List<VersionVertex> VersionVertices()
        {
            var vertices = new List<VersionVertex>();
            if (Version != null)
            {
                vertices.Add(new ExactVersionVertex(Version.ToVersion()));
            }
            if (MinVersion != null)
            {
                vertices.Add(new GreaterThenOrEqualVersionVertex(MinVersion.ToVersion()));
            }
            if (MaxVersion != null)
            {
                vertices.Add(new LessThanVersionVertex(MaxVersion.ToVersion()));
            }
            if (Version == null || MinVersion == null || MaxVersion == null)
            {
                vertices.Add(new AnyVersionVertex());
            }
            return vertices;
        }


        ICommandOutput VerifyWrapFile()
        {
            if (NoDescriptorUpdate)
                return new GenericMessage("Wrap descriptor ignored.");
            return Environment.Descriptor == null
                       ? new GenericMessage(@"No wrap descriptor found, installing locally.")
                       : new GenericMessage("Wrap descriptor found.");
        }

        ICommandOutput VeryfyWrapRepository()
        {
            return Environment.ProjectRepository != null
                       ? new GenericMessage("Project repository present.")
                       : new GenericMessage("Project repository not found.");
        }
    }
    public static class DependencyResolutionResultExtensions
    {
        public static IEnumerable<ICommandOutput> GacConflicts(this DependencyResolutionResult result, ExecutionEnvironment env)
        {
            return from package in GacResolver.InGac(result.Dependencies.Select(x => x.Package), env)
                   from assembly in package
                   select new GacConflict(package.Key, assembly) as ICommandOutput;
        }
    }
}