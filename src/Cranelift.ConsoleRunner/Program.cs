﻿using Cranelift.Common;
using Cranelift.Common.Abstractions;
using Cranelift.Common.Helpers;
using Cranelift.Common.Models;

using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Cranelift.ConsoleRunner
{
    class FileBlobStorage : IBlobStorage
    {
        private readonly string _inputDir;
        private readonly string _outputDir;

        public FileBlobStorage(string inputDir, string outputDir)
        {
            _inputDir = inputDir;
            _outputDir = outputDir;
        }

        public async Task DownloadBlobs(int userId, string jobId, Func<string, Stream> getDestinationStream, CancellationToken cancellationToken)
        {
            var files = Directory.EnumerateFiles(_inputDir);
            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var outputStream = getDestinationStream(Path.GetFileName(file));
                using (var inputStream = File.OpenRead(file))
                    await inputStream.CopyToAsync(outputStream);
            }
        }

        public async Task<bool> UploadBlob(int userId, string jobId, string name, Stream data, string contentType, CancellationToken cancellationToken)
        {
            try
            {
                var fullPath = Path.Combine(_outputDir, name);
                using (var file = File.OpenWrite(fullPath))
                {
                    await data.CopyToAsync(file);
                }

                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }

    public class Program
    {
        public static async Task Main(string[] args)
        {
            var xml = File.ReadAllText("assets/table.xml");
            var tables = ICDAR19TableParser.ParseDocument(xml);

            if (args.Length < 2)
            {
                Console.WriteLine("Usage: cranelift inputDir outputDir [langs]");
                return;
            }

            var inputDir = args[0];
            var outputDir = args[1];
            var langs = "ckb";
            if (args.Length >= 3)
                langs = args[2];

            var job = new Job
            {
                Id = Guid.NewGuid().ToString("N"),
                UserId = 1,
                Lang = langs,
                Status = ModelConstants.Queued,
            };

            var pythonOptions = new PythonOptions
            {
                Path = "python3"
            };

            var files = Directory.EnumerateFiles(inputDir).ToArray();
            var tempFolder = Path.Combine(Path.GetTempPath(), "cranelift", "original", job.UserId.ToString(), job.Id);
            Directory.CreateDirectory(tempFolder);
            int i = 0;
            foreach (var file in files)
            {
                var destinationPath = Path.Combine(tempFolder, i + Path.GetExtension(file));
                File.Copy(file, destinationPath);

                i++;
            }

            var pipeline = new OcrPipeline(
                line => Console.WriteLine(line),
                new FileBlobStorage(inputDir, outputDir),
                new DocumentHelper(),
                new PythonHelper(pythonOptions),
                new OcrPipelineOptions
                {
                    WorkerCount = 1,
                    ParallelPagesCount = 1,
                    TesseractModelsDirectory = Path.GetFullPath("dependencies/models"),
                    ZhirPyDirectory = Path.GetFullPath("dependencies/zhirpy"),
                });

            var result = await pipeline.RunAsync(job, default);

            if (result.Status == OcrPipelineStatus.Completed)
            {
                for (int x = 0; x < result.Pages.Length; x++)
                {
                    var page = result.Pages[x];
                    var hocrPage = result.HocrPages[x];

                    var imagePath = files[x];

                    var bitmap = (Bitmap)Image.FromFile(imagePath);
                    bitmap = DrawBoxes(bitmap, hocrPage, tables);

                    var destinationImage = Path.Combine(outputDir, Path.GetFileNameWithoutExtension(page.Name) + ".boxes.jpg");
                    bitmap.Save(destinationImage);
                }
            }

            try
            {
                Directory.Delete(tempFolder, recursive: true);
            }
            catch (Exception) { }

            Console.WriteLine($"Result: {result.Status}");
        }

        private static Bitmap DrawBoxes(Bitmap bitmap, HocrPage hocrPage, IEnumerable<ICDAR19Table> tables)
        {
            var paragraphPen = Pens.Red;
            var linePen = Pens.Green;
            var wordPen = Pens.Blue;

            var tablePen = Pens.Purple;
            var cellPen = Pens.Magenta;

            using (var graphics = Graphics.FromImage(bitmap))
            {
                foreach (var paragaph in hocrPage.Paragraphs)
                {
                    graphics.DrawRectangle(paragraphPen, ToRectangle(paragaph.BoundingBox.Value));

                    foreach (var line in paragaph.Lines)
                    {
                        graphics.DrawRectangle(linePen, ToRectangle(line.BoundingBox.Value));
                        foreach (var word in line.Words)
                        {
                            graphics.DrawRectangle(wordPen, ToRectangle(word.BoundingBox.Value));
                        }
                    }
                }

                foreach (var table in tables)
                {
                    graphics.DrawRectangle(tablePen, ToRectangle(table.BoundingBox));

                    foreach (var cell in table.Cells)
                    {
                        graphics.DrawRectangle(cellPen, ToRectangle(cell.BoundingBox));
                    }
                }
            }

            return bitmap;
        }

        private static Rectangle ToRectangle(Rect hocrRect)
        {
            return new Rectangle((int)hocrRect.X, (int)hocrRect.Y, (int)hocrRect.Width, (int)hocrRect.Height);
        }
    }
}
