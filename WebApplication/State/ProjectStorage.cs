﻿using System;
using System.IO;
using System.IO.Compression;
using System.Threading.Tasks;
using WebApplication.Definitions;
using WebApplication.Utilities;

namespace WebApplication.State
{
    /// <summary>
    /// Logic related to local cache storage for project.
    /// </summary>
    public class ProjectStorage
    {
        /// <summary>
        /// Project names.
        /// </summary>
        public Project Project { get; }

        /// <summary>
        /// Project metadata.
        /// </summary>
        public ProjectMetadata Metadata => _lazyMetadata.Value;
        private readonly Lazy<ProjectMetadata> _lazyMetadata;

        /// <summary>
        /// Constructor.
        /// </summary>
        public ProjectStorage(Project project)
        {
            Project = project;

            _lazyMetadata = new Lazy<ProjectMetadata>(() =>
                                                            {
                                                                var metadataFile = Project.LocalAttributes.Metadata;
                                                                if (!File.Exists(metadataFile))
                                                                    throw new ApplicationException("Attempt to work with uninitialized project storage.");

                                                                return Json.DeserializeFile<ProjectMetadata>(metadataFile);
                                                            });
        }

        /// <summary>
        /// Ensure the project is cached locally.
        /// </summary>
        public async Task EnsureLocalAsync(OssBucket ossBucket)
        {
            // ensure the directory exists
            Directory.CreateDirectory(Project.LocalAttributes.BaseDir);

            // download metadata and thumbnail
            await Task.WhenAll(
                                ossBucket.DownloadFileAsync(Project.OssAttributes.Metadata, Project.LocalAttributes.Metadata),
                                ossBucket.DownloadFileAsync(Project.OssAttributes.Thumbnail, Project.LocalAttributes.Thumbnail)
                            );


            // download ZIP with SVF model
            // NOTE: this step is impossible without having project metadata,
            // because file/dir names depends on hash of initial project state

            await PlaceViewablesAsync(ossBucket, GetLocalNames(), GetOssNames());
        }

        /// <summary>
        /// Ensure the project viewables are cached locally.
        /// </summary>
        /// <param name="ossBucket">OSS bucket.</param>
        /// <param name="hash">Parameters hash.</param>
        public Task EnsureViewablesAsync(OssBucket ossBucket, string hash)
        {
            return PlaceViewablesAsync(ossBucket, GetLocalNames(hash), GetOssNames(hash));
        }

        private async Task PlaceViewablesAsync(OssBucket ossBucket, LocalNameProvider localNames, OSSObjectNameProvider ossNames)
        {
            // create the "hashed" dir
            Directory.CreateDirectory(localNames.BaseDir);

            using var tempFile = new TempFile();
            await Task.WhenAll(
                                ossBucket.DownloadFileAsync(ossNames.ModelView, tempFile.Name),
                                ossBucket.DownloadFileAsync(ossNames.Parameters, localNames.Parameters)
                            );

            // extract SVF from the archive
            ZipFile.ExtractToDirectory(tempFile.Name, localNames.SvfDir, overwriteFiles: true); // TODO: non-default encoding is not supported
        }


        /// <summary>
        /// OSS names for "hashed" files.
        /// </summary>
        public OSSObjectNameProvider GetOssNames(string hash = null) => Project.OssNameProvider(hash ?? Metadata.Hash);

        /// <summary>
        /// Local names for "hashed" files.
        /// </summary>
        public LocalNameProvider GetLocalNames(string hash = null) => Project.LocalNameProvider(hash ?? Metadata.Hash);
    }
}