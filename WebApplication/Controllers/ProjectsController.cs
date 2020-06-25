﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Autodesk.Forge.Model;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using WebApplication.Definitions;
using WebApplication.Middleware;
using WebApplication.Processing;
using WebApplication.State;
using WebApplication.Utilities;
using Project = WebApplication.State.Project;

namespace WebApplication.Controllers
{
    [ApiController]
    [Route("projects")]
    public class ProjectsController : ControllerBase
    {
        private readonly ILogger<ProjectsController> _logger;
        private readonly DtoGenerator _dtoGenerator;
        private readonly UserResolver _userResolver;
        private readonly LocalCache _localCache;
        private readonly ProjectWork _projectWork;

        public ProjectsController(ILogger<ProjectsController> logger, DtoGenerator dtoGenerator, UserResolver userResolver,
            LocalCache localCache, ProjectWork projectWork)
        {
            _logger = logger;
            _dtoGenerator = dtoGenerator;
            _userResolver = userResolver;
            _localCache = localCache;
            _projectWork = projectWork;
        }

        [HttpGet("")]
        public async Task<IEnumerable<ProjectDTO>> ListAsync()
        {
            var bucket = await _userResolver.GetBucket(tryToCreate: true);

            // TODO move to projects repository?
            List<ObjectDetails> objects = await bucket.GetObjectsAsync($"{ONC.ProjectsFolder}-");

            var projectDTOs = new List<ProjectDTO>();
            foreach(ObjectDetails objDetails in objects)
            {
                var projectName = ONC.ToProjectName(objDetails.ObjectKey);

                // TODO: in future bad projects should not affect project listing. It's a workaround
                try
                {
                    ProjectStorage projectStorage = await _userResolver.GetProjectStorageAsync(projectName); // TODO: expensive to do it in the loop

                    projectDTOs.Add(ToDTO(projectStorage));
                }
                catch (Exception e)
                {
                    // log, swallow and continue (see the comment above)
                    _logger.LogWarning(e, $"Ignoring '{projectName}' project, which (seems) failed to adopt.");
                }
            }

            return projectDTOs;
        }

        [HttpPost]
        public async Task<ActionResult<ProjectDTO>> CreateProject([FromForm]NewProjectModel projectModel)
        {
            var projectName = Path.GetFileNameWithoutExtension(projectModel.package.FileName);
            var bucket = await _userResolver.GetBucket(true);

            // Check if project already exists
            List<ObjectDetails> objects = await bucket.GetObjectsAsync($"{ONC.ProjectsFolder}-");
            foreach(ObjectDetails objDetails in objects)
            {
                var existingProjectName = ONC.ToProjectName(objDetails.ObjectKey);
                if (projectName == existingProjectName)
                {
                    return Conflict();
                }
            }

            var projectInfo = new ProjectInfo
            {
                Name = projectName,
                TopLevelAssembly = projectModel.root
            };
            
            // download file locally (a place to improve... would be good to stream it directly to OSS)
            using var file = new TempFile();
            await using (var fileWriteStream = System.IO.File.OpenWrite(file.Name))
            {
                await projectModel.package.CopyToAsync(fileWriteStream);
            }

            // upload the file to OSS
            ProjectStorage projectStorage = await _userResolver.GetProjectStorageAsync(projectName);

            string ossSourceModel = projectStorage.Project.OSSSourceModel;
            await using (var fileReadStream = System.IO.File.OpenRead(file.Name))
            {
                // determine if we need to upload in chunks or in one piece
                long sizeToUpload = fileReadStream.Length;
                long chunkMBSize = 5;
                long chunkSize = chunkMBSize * 1024 * 1024; // 2MB is minimal

                // use chunks for all files greater than chunk size
                if (sizeToUpload > chunkSize)
                {
                    _logger.LogInformation($"Uploading in {chunkMBSize}MB chunks");

                    string sessionId = Guid.NewGuid().ToString();
                    long begin = 0;
                    byte[] buffer = new byte[chunkSize];
                    int bytesRead = 0;

                    while (begin < sizeToUpload-1)
                    {
                        bytesRead = await fileReadStream.ReadAsync(buffer, 0, (int)chunkSize);
                        int memoryStreamSize = sizeToUpload - begin < chunkSize ? (int)(sizeToUpload - begin) : (int)(chunkSize);
                        using (MemoryStream chunkStream = new MemoryStream(buffer, 0, memoryStreamSize))
                        {
                            string contentRange = string.Format($"bytes {begin}-{begin + bytesRead -1}/{sizeToUpload}");
                            await bucket.UploadChunkAsync(ossSourceModel, contentRange, sessionId, chunkStream);
                        }
                        begin += bytesRead;
                    }
                }
                else
                {
                    await bucket.UploadObjectAsync(ossSourceModel, fileReadStream);
                }
            }

            bool adopted = false;

            // adopt the project
            try
            {
                string signedUrl = await bucket.CreateSignedUrlAsync(ossSourceModel);
                await _projectWork.AdoptAsync(projectInfo, signedUrl);

                adopted = true;
            }
            catch (FdaProcessingException fpe)
            {
                var result = new ResultDTO
                {
                    Success = false,
                    Message = fpe.Message,
                    ReportUrl = fpe.ReportUrl
                };
                return UnprocessableEntity(result);
            }
            finally
            {
                // on any failure during adoption we consider that project adoption failed and it's not usable
                if (! adopted)
                {
                    await bucket.DeleteObjectAsync(ossSourceModel);
                }
            }

            return Ok(ToDTO(projectStorage));
        }

        /// <summary>
        /// Generate project DTO.
        /// </summary>
        private ProjectDTO ToDTO(ProjectStorage projectStorage)
        {
            Project project = projectStorage.Project;

            var dto = _dtoGenerator.MakeProjectDTO<ProjectDTO>(project, projectStorage.Metadata.Hash);
            dto.Id = project.Name;
            dto.Label = project.Name;
            dto.Image = _localCache.ToDataUrl(project.LocalAttributes.Thumbnail);
            return dto;
        }
    }
}
