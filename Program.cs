using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Globalization;
using System.Buffers.Binary;

namespace CidbTool;

internal static class Program
{
    private static string ToolVersion =>
        typeof(Program).Assembly.GetName().Version is { } version ? $"{version.Major}.{version.Minor}.{version.Build}" : "?";
    private const int BfresHeaderSize = 0x20;
    private const int RelocationOffsetField = 0x18;
    private static readonly byte[] BfresMagic = Encoding.ASCII.GetBytes("FRES");
    private static readonly byte[] CidbMagic = Encoding.ASCII.GetBytes("CIDB");
    private static readonly byte[] NxsMeshMagic = new byte[] { 0x4E, 0x58, 0x53, 0x01, 0x4D, 0x45, 0x53, 0x48 }; // NXS\x01MESH
    private static readonly byte[] RtreeMagic = Encoding.ASCII.GetBytes("RTRE");
    private const uint MeshFlagMaterialIndices = 0x01;
    private const uint MeshFlagFaceRemap = 0x02;
    private const uint MeshFlag8BitIndices = 0x04;
    private const uint MeshFlag16BitIndices = 0x08;

    private static int Main(string[] args)
    {
        try
        {
            if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
            {
                PrintHelp();
                return 0;
            }
            if (args[0] is "--version" or "version")
            {
                Console.WriteLine($"CidbTool {ToolVersion}");
                return 0;
            }

            return args[0].ToLowerInvariant() switch
            {
                "inspect" => InspectCommand(args),
                "extract" => ExtractCommand(args),
                "decode" => DecodeCommand(args),
                "rebuild-surface" => RebuildSurfaceCommand(args),
                "rebuild-geometry" => RebuildGeometryCommand(args),
                "translate" => TranslateCommand(args),
                "cook-blender" => CookBlenderCommand(args),
                "cook-blender-leaf4" => CookBlenderLeaf4Command(args),
                "cook-blender-spatial" => CookBlenderSpatialCommand(args),
                "cook-blender-physx" => CookBlenderPhysxCommand(args),
                "verify-cooker" => VerifyCookerCommand(args),
                "verify-recook" => VerifyRecookCommand(args),
                "verify-edges" => VerifyEdgesCommand(args),
                "export-presets" => ExportPresetsCommand(args),
                "cook-blender-physx-layout" => CookBlenderPhysxLayoutCommand(args),
                "cook-blender-physx-quick" => CookBlenderPhysxQuickCommand(args),
                "cook-blender-template-tree" => CookBlenderTemplateTreeCommand(args),
                "cook-blender-layout" => CookBlenderLayoutCommand(args),
                "create-topology-smoke-test" => CreateTopologySmokeTestCommand(args),
                "create-barrier-smoke-test" => CreateBarrierSmokeTestCommand(args),
                "rebuild-rtree-layout" => RebuildRtreeLayoutCommand(args),
                "restore-mesh" => RestoreMeshCommand(args),
                "batch" => BatchCommand(args),
                "surface-catalog" => SurfaceCatalogCommand(args),
                "contact-catalog" => ContactCatalogCommand(args),
                "audit" => AuditCommand(args),
                "export-surface-layers" => ExportSurfaceLayersCommand(args),
                "create-floor-surface-tests" => CreateFloorSurfaceTestsCommand(args),
                "wrap-toolbox" => WrapToolboxCommand(args),
                _ => Fail($"Unknown command '{args[0]}'."),
            };
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Error: {exception.Message}");
            return 1;
        }
    }

    private static int InspectCommand(string[] args)
    {
        if (args.Length != 2)
            return Fail("Usage: inspect <room.bfres>");

        var report = Inspect(Path.GetFullPath(args[1]));
        PrintReport(report);
        return 0;
    }

    private static int ExtractCommand(string[] args)
    {
        if (args.Length is < 2 or > 3)
            return Fail("Usage: extract <room.bfres> [output-directory]");

        var inputPath = Path.GetFullPath(args[1]);
        var outputDirectory = args.Length == 3
            ? Path.GetFullPath(args[2])
            : Path.Combine(Path.GetDirectoryName(inputPath)!, "_cidb");
        var report = Inspect(inputPath);
        WriteExtraction(inputPath, outputDirectory, report);
        return 0;
    }

    private static int BatchCommand(string[] args)
    {
        if (args.Length is < 2 or > 3)
            return Fail("Usage: batch <map-directory> [output-directory]");

        var mapDirectory = Path.GetFullPath(args[1]);
        if (!Directory.Exists(mapDirectory))
            return Fail($"Directory not found: {mapDirectory}");

        var outputDirectory = args.Length == 3
            ? Path.GetFullPath(args[2])
            : Path.Combine(mapDirectory, "_cidb");
        var files = Directory.EnumerateFiles(mapDirectory, "*.bfres", SearchOption.TopDirectoryOnly)
            .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var successful = 0;

        foreach (var path in files)
        {
            try
            {
                var report = Inspect(path);
                if (report.Blocks.Count == 0)
                {
                    Console.WriteLine($"SKIP  {Path.GetFileName(path)} (no CIDB block)");
                    continue;
                }

                WriteExtraction(path, outputDirectory, report);
                successful++;
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine($"FAIL  {Path.GetFileName(path)}: {exception.Message}");
            }
        }

        Console.WriteLine($"Completed: {successful} BFRES file(s) with CIDB extracted to {outputDirectory}");
        return successful == 0 && files.Length != 0 ? 1 : 0;
    }

    private static int SurfaceCatalogCommand(string[] args)
    {
        if (args.Length is < 2 or > 3)
            return Fail("Usage: surface-catalog <map-directory> [output.json]");

        var mapDirectory = Path.GetFullPath(args[1]);
        if (!Directory.Exists(mapDirectory))
            return Fail($"Directory not found: {mapDirectory}");

        var outputPath = args.Length == 3
            ? Path.GetFullPath(args[2])
            : Path.Combine(mapDirectory, "cidb-surface-catalog.json");
        var files = Directory.EnumerateFiles(mapDirectory, "*.bfres", SearchOption.TopDirectoryOnly)
            .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var surfaces = new Dictionary<ushort, SurfaceCatalogAccumulator>();
        var failures = new List<string>();
        var cidbFiles = 0;
        var cidbBlocks = 0;
        var meshCount = 0;

        foreach (var path in files)
        {
            try
            {
                var inputs = LoadCidbInputs(path);
                if (inputs.Count == 0)
                    continue;

                cidbFiles++;
                foreach (var input in inputs)
                {
                    var geometry = DecodeGeometry(input.Name, input.Data);
                    cidbBlocks++;
                    meshCount += geometry.Meshes.Count;
                    foreach (var mesh in geometry.Meshes)
                    {
                        foreach (var group in mesh.SurfaceIds.GroupBy(id => id))
                        {
                            if (!surfaces.TryGetValue(group.Key, out var surface))
                            {
                                surface = new SurfaceCatalogAccumulator(group.Key);
                                surfaces.Add(group.Key, surface);
                            }

                            surface.Add(Path.GetFileName(path), mesh.Index, group.Count());
                        }
                    }
                }
            }
            catch (Exception exception)
            {
                // A non-CIDB BFRES is normal. Only preserve actual parse failures as
                // evidence, rather than aborting a catalog of the rest of the ROMFS.
                if (!exception.Message.Contains("No CIDB", StringComparison.OrdinalIgnoreCase))
                    failures.Add($"{Path.GetFileName(path)}: {exception.Message}");
            }
        }

        var report = new SurfaceCatalogReport(
            mapDirectory,
            files.Length,
            cidbFiles,
            cidbBlocks,
            meshCount,
            surfaces.Values
                .OrderBy(item => item.SurfaceId)
                .Select(item => item.ToReport())
                .ToArray(),
            failures);
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        File.WriteAllText(outputPath, JsonSerializer.Serialize(report, JsonOptions));
        Console.WriteLine($"WRITE {outputPath}");
        Console.WriteLine($"Scanned {files.Length:N0} BFRES; decoded {cidbBlocks:N0} CIDB block(s), {meshCount:N0} mesh(es), and found {surfaces.Count:N0} raw surface ID(s).");
        if (failures.Count != 0)
            Console.WriteLine($"Note: {failures.Count:N0} file(s) could not be catalogued; see the JSON report.");
        return 0;
    }

    private static int ContactCatalogCommand(string[] args)
    {
        if (args.Length is < 2 or > 3)
            return Fail("Usage: contact-catalog <map-directory> [output.json]");

        var mapDirectory = Path.GetFullPath(args[1]);
        if (!Directory.Exists(mapDirectory))
            return Fail($"Directory not found: {mapDirectory}");
        var outputPath = args.Length == 3
            ? Path.GetFullPath(args[2])
            : Path.Combine(mapDirectory, "cidb-contact-catalog.json");
        var files = Directory.EnumerateFiles(mapDirectory, "*.bfres", SearchOption.TopDirectoryOnly)
            .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var codes = new Dictionary<uint, ContactCodeAccumulator>();
        var failures = new List<string>();
        var cidbBlocks = 0;
        var meshCount = 0;

        foreach (var path in files)
        {
            try
            {
                foreach (var input in LoadCidbInputs(path))
                {
                    var geometry = DecodeGeometry(input.Name, input.Data);
                    cidbBlocks++;
                    meshCount += geometry.Meshes.Count;
                    foreach (var mesh in geometry.Meshes)
                    {
                        foreach (var pair in mesh.MaterialCodes.Select((code, slot) => (code, slot)))
                        {
                            if (!codes.TryGetValue(pair.code, out var accumulator))
                            {
                                accumulator = new ContactCodeAccumulator(pair.code);
                                codes.Add(pair.code, accumulator);
                            }
                            accumulator.Add(Path.GetFileName(path), mesh.Index, pair.slot, mesh.SurfaceIds.Count(id => id == pair.slot));
                        }
                    }
                }
            }
            catch (Exception exception)
            {
                if (!exception.Message.Contains("No CIDB", StringComparison.OrdinalIgnoreCase))
                    failures.Add($"{Path.GetFileName(path)}: {exception.Message}");
            }
        }

        var report = new ContactCatalogReport(
            mapDirectory,
            files.Length,
            cidbBlocks,
            meshCount,
            codes.Values.OrderBy(item => item.Code).Select(item => item.ToReport()).ToArray(),
            failures);
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        File.WriteAllText(outputPath, JsonSerializer.Serialize(report, JsonOptions));
        Console.WriteLine($"WRITE {outputPath}");
        Console.WriteLine($"Scanned {files.Length:N0} BFRES; decoded {cidbBlocks:N0} CIDB block(s), {meshCount:N0} mesh(es), and found {codes.Count:N0} full contact code(s).");
        if (failures.Count != 0)
            Console.WriteLine($"Note: {failures.Count:N0} file(s) could not be catalogued; see the JSON report.");
        return 0;
    }

    private static int AuditCommand(string[] args)
    {
        if (args.Length is < 2 or > 3)
            return Fail("Usage: audit <map-directory|room.bfres> [output.json]");

        var inputPath = Path.GetFullPath(args[1]);
        var files = File.Exists(inputPath)
            ? new[] { inputPath }
            : Directory.Exists(inputPath)
                ? Directory.EnumerateFiles(inputPath, "*.bfres", SearchOption.TopDirectoryOnly).OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase).ToArray()
                : throw new DirectoryNotFoundException($"Directory or BFRES file not found: {inputPath}");
        var outputPath = args.Length == 3
            ? Path.GetFullPath(args[2])
            : Path.Combine(File.Exists(inputPath) ? Path.GetDirectoryName(inputPath)! : inputPath, "cidb-structural-audit.json");

        var containers = new List<CidbAuditContainer>();
        var containerFlags = new Dictionary<uint, int>();
        var nxsHeaders = new Dictionary<string, int>(StringComparer.Ordinal);
        var descriptorWords = Enumerable.Range(0, 11).Select(_ => new Dictionary<uint, int>()).ToArray();
        var extraFlags = new Dictionary<byte, int>();
        var surfaceExtraPairs = new Dictionary<string, int>(StringComparer.Ordinal);
        var rtreeHeaders = Enumerable.Range(0, 6).Select(_ => new Dictionary<uint, int>()).ToArray();
        var failures = new List<string>();
        var filesScanned = 0;

        foreach (var path in files)
        {
            filesScanned++;
            try
            {
                foreach (var input in LoadCidbInputs(path))
                {
                    var cidb = input.Data;
                    AddCount(containerFlags, ReadUInt32(cidb, 8));
                    var sections = ReadRawCidbSections(cidb);
                    for (var sectionIndex = 0; sectionIndex < sections.Count; sectionIndex++)
                    {
                        var descriptorOffset = checked((int)ReadUInt32(cidb, 0x10 + sectionIndex * 4));
                        for (var word = 0; word < 11; word++)
                            AddCount(descriptorWords[word], ReadUInt32(cidb, descriptorOffset + word * 4));
                    }

                    var geometry = DecodeGeometry(input.Name, cidb);
                    var meshes = new List<CidbAuditMesh>();
                    foreach (var mesh in geometry.Meshes)
                    {
                        var header0 = ReadUInt32(cidb, mesh.Offset + 8);
                        var header1 = ReadUInt32(cidb, mesh.Offset + 12);
                        var header2 = ReadUInt32(cidb, mesh.Offset + 16);
                        AddCount(nxsHeaders, $"{header0:X8}:{header1:X8}:{header2:X8}");
                        for (var word = 0; word < 6; word++)
                            AddCount(rtreeHeaders[word], ReadUInt32BigEndian(cidb, mesh.RtreeOffset + 4 + word * 4));
                        foreach (var pair in mesh.SurfaceIds.Zip(mesh.ExtraFlags))
                        {
                            AddCount(extraFlags, pair.Second);
                            AddCount(surfaceExtraPairs, $"{pair.First}:{pair.Second}");
                        }

                        var sectionIndex = Enumerable.Range(0, sections.Count)
                            .FirstOrDefault(index => ReadUInt32(cidb, 0x10 + index * 4) == mesh.Offset - 0x2C) + 1;
                        meshes.Add(new CidbAuditMesh(
                            mesh.Index,
                            sectionIndex,
                            mesh.Vertices.Count,
                            mesh.Triangles.Count,
                            header0,
                            header1,
                            header2,
                            mesh.RtreePreludeFirstWord,
                            mesh.RtreePreludeLength,
                            mesh.RtreePreludeLength == mesh.Triangles.Count * RemapWidth(mesh.RtreePreludeFirstWord) + 4,
                            mesh.SurfaceIds.GroupBy(value => value).OrderBy(group => group.Key).Select(group => new ValueCount<ushort>(group.Key, group.Count())).ToArray(),
                            mesh.ExtraFlags.GroupBy(value => value).OrderBy(group => group.Key).Select(group => new ValueCount<byte>(group.Key, group.Count())).ToArray()));
                    }
                    containers.Add(new CidbAuditContainer(Path.GetFileName(path), input.Name, cidb.Length, ReadUInt32(cidb, 8), sections.Count, meshes));
                }
            }
            catch (Exception exception)
            {
                if (!exception.Message.Contains("No CIDB", StringComparison.OrdinalIgnoreCase))
                    failures.Add($"{Path.GetFileName(path)}: {exception.Message}");
            }
        }

        var report = new CidbAuditReport(
            inputPath,
            filesScanned,
            containers,
            ToCounts(containerFlags),
            ToCounts(nxsHeaders),
            descriptorWords.Select(ToCounts).ToArray(),
            ToCounts(extraFlags),
            ToCounts(surfaceExtraPairs),
            rtreeHeaders.Select(ToCounts).ToArray(),
            failures);
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        File.WriteAllText(outputPath, JsonSerializer.Serialize(report, JsonOptions));
        Console.WriteLine($"WRITE {outputPath}");
        Console.WriteLine($"Scanned {filesScanned:N0} BFRES; audited {containers.Count:N0} CIDB container(s), {containers.Sum(container => container.Meshes.Count):N0} NXS/MESH block(s).");
        return 0;
    }

    private static void AddCount<T>(Dictionary<T, int> counts, T value) where T : notnull
    {
        counts[value] = counts.TryGetValue(value, out var current) ? current + 1 : 1;
    }

    private static IReadOnlyList<ValueCount<T>> ToCounts<T>(Dictionary<T, int> counts) where T : notnull
    {
        return counts.OrderBy(pair => pair.Key).Select(pair => new ValueCount<T>(pair.Key, pair.Value)).ToArray();
    }

    private static int ExportSurfaceLayersCommand(string[] args)
    {
        if (args.Length is < 2 or > 3)
            return Fail("Usage: export-surface-layers <room.bfres|extracted.cidb> [output-directory]");

        var inputPath = Path.GetFullPath(args[1]);
        if (!File.Exists(inputPath))
            return Fail($"File not found: {inputPath}");
        var outputDirectory = args.Length == 3
            ? Path.GetFullPath(args[2])
            : Path.Combine(Path.GetDirectoryName(inputPath)!, "_cidb_surface_layers");
        var geometries = LoadCidbInputs(inputPath)
            .Select(input => DecodeGeometry(input.Name, input.Data))
            .ToArray();
        if (geometries.Length == 0)
            return Fail("No CIDB block was found in this BFRES.");

        Directory.CreateDirectory(outputDirectory);
        foreach (var geometry in geometries)
            WriteSurfaceLayers(outputDirectory, geometry);
        return 0;
    }

    private static int CreateFloorSurfaceTestsCommand(string[] args)
    {
        if (args.Length != 3)
            return Fail("Usage: create-floor-surface-tests <source.cidb> <output-directory>");

        var sourcePath = Path.GetFullPath(args[1]);
        var outputDirectory = Path.GetFullPath(args[2]);
        var source = LoadCidbContainer(sourcePath);
        var geometry = DecodeGeometry(Path.GetFileNameWithoutExtension(sourcePath), source.CidbData);
        var patch = FindLargestFloorPatch(geometry);
        if (patch.Count == 0)
            return Fail("No upward-facing surface-ID-0 floor patch was found. Choose a different room or use a manual surface-only OBJ edit.");

        Directory.CreateDirectory(outputDirectory);
        var outputs = new List<FloorSurfaceTestOutput>();
        for (ushort targetId = 1; targetId <= 10; targetId++)
        {
            var output = source.OriginalData.ToArray();
            foreach (var triangle in patch)
                WriteUInt16(output, source.CidbOffset + triangle.SurfaceIdsOffset + triangle.TriangleIndex * 2, targetId);
            var outputPath = Path.Combine(outputDirectory, $"{geometry.Name}.floor-patch.surface_{targetId:D2}.cidb");
            File.WriteAllBytes(outputPath, output);
            var toolboxPath = Path.Combine(outputDirectory, $"{geometry.Name}.floor-patch.surface_{targetId:D2}.toolbox.cidb");
            File.WriteAllBytes(toolboxPath, ToToolboxCidb(source, output));
            outputs.Add(new FloorSurfaceTestOutput(targetId, Path.GetFileName(toolboxPath)));
        }

        var manifest = new FloorSurfaceTestManifest(
            sourcePath,
            geometry.Name,
            "Only the listed raw per-triangle u16 surface IDs differ from the source. Vertices, triangle indices, face remaps, RTRE pages, mesh AABBs, extra triangle data, and all unknown bytes are preserved byte-for-byte.",
            patch.Select(item => new FloorPatchTriangle(item.MeshIndex, item.TriangleIndex, item.Center)).ToArray(),
            outputs);
        File.WriteAllText(Path.Combine(outputDirectory, $"{geometry.Name}.floor-patch.tests.json"), JsonSerializer.Serialize(manifest, JsonOptions));
        Console.WriteLine($"WRITE {outputDirectory}  {outputs.Count} CIDB variants; each repaints {patch.Count} upward-facing floor triangle(s).");
        return 0;
    }

    private static int WrapToolboxCommand(string[] args)
    {
        if (args.Length != 3)
            return Fail("Usage: wrap-toolbox <raw-or-toolbox.cidb> <output.cidb>");

        var sourcePath = Path.GetFullPath(args[1]);
        var outputPath = Path.GetFullPath(args[2]);
        if (string.Equals(sourcePath, outputPath, StringComparison.OrdinalIgnoreCase))
            return Fail("Output CIDB must be a new file; the source is never overwritten.");
        var source = LoadCidbContainer(sourcePath);
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        File.WriteAllBytes(outputPath, ToToolboxCidb(source, source.OriginalData));
        Console.WriteLine($"WRITE {outputPath}");
        Console.WriteLine("CIDB was emitted in Switch Toolbox ExternalFile form: 04 00 00 00 followed by the CIDB container.");
        return 0;
    }

    private static int DecodeCommand(string[] args)
    {
        if (args.Length is < 2 or > 3)
            return Fail("Usage: decode <room.bfres|extracted.cidb> [output-directory]");

        var inputPath = Path.GetFullPath(args[1]);
        if (!File.Exists(inputPath))
            return Fail($"File not found: {inputPath}");
        var outputDirectory = args.Length == 3
            ? Path.GetFullPath(args[2])
            : Path.Combine(Path.GetDirectoryName(inputPath)!, "_cidb_geometry");

        var decoded = LoadCidbInputs(inputPath)
            .Select(input => DecodeGeometry(input.Name, input.Data))
            .ToArray();
        if (decoded.Length == 0)
            return Fail("No CIDB block was found in this BFRES.");

        Directory.CreateDirectory(outputDirectory);
        foreach (var geometry in decoded)
            WriteGeometry(outputDirectory, geometry);
        return 0;
    }

    private static int RebuildSurfaceCommand(string[] args)
    {
        if (args.Length != 4)
            return Fail("Usage: rebuild-surface <source.cidb> <edited.collision[.blender].obj> <output.cidb>");

        var sourcePath = Path.GetFullPath(args[1]);
        var objPath = Path.GetFullPath(args[2]);
        var outputPath = Path.GetFullPath(args[3]);
        if (string.Equals(sourcePath, outputPath, StringComparison.OrdinalIgnoreCase))
            return Fail("Output CIDB must be a new file; the source CIDB is never overwritten.");

        var source = LoadCidbContainer(sourcePath);
        var geometry = DecodeGeometry(Path.GetFileNameWithoutExtension(sourcePath), source.CidbData);
        var obj = ParseObj(objPath);
        var surfaceIds = ValidateSurfaceOnlyObj(geometry, obj);

        var output = source.OriginalData.ToArray();
        var modifiedTriangles = 0;
        foreach (var mesh in geometry.Meshes)
        {
            var editedIds = surfaceIds[mesh.Index];
            for (var triangle = 0; triangle < mesh.Triangles.Count; triangle++)
            {
                if (editedIds[triangle] != mesh.SurfaceIds[triangle])
                    modifiedTriangles++;
                WriteUInt16(output, source.CidbOffset + mesh.SurfaceIdsOffset + triangle * 2, editedIds[triangle]);
            }
        }

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        File.WriteAllBytes(outputPath, output);
        Console.WriteLine($"WRITE {outputPath}");
        Console.WriteLine($"Validated {geometry.Meshes.Count} mesh(es); changed {modifiedTriangles} triangle surface ID(s). Geometry, topology, face remap, and RTRE were preserved byte-for-byte.");
        return 0;
    }

    private static int RebuildGeometryCommand(string[] args)
    {
        if (args.Length != 4)
            return Fail("Usage: rebuild-geometry <source.cidb> <edited.collision[.blender].obj> <output.cidb>");

        var sourcePath = Path.GetFullPath(args[1]);
        var objPath = Path.GetFullPath(args[2]);
        var outputPath = Path.GetFullPath(args[3]);
        if (string.Equals(sourcePath, outputPath, StringComparison.OrdinalIgnoreCase))
            return Fail("Output CIDB must be a new file; the source CIDB is never overwritten.");

        var source = LoadCidbContainer(sourcePath);
        var geometry = DecodeGeometry(Path.GetFileNameWithoutExtension(sourcePath), source.CidbData);
        var obj = ParseObj(objPath);
        var edits = ValidateGeometryObj(geometry, obj);
        var output = source.OriginalData.ToArray();
        var changedVertices = 0;
        var changedSurfaceIds = 0;
        var refitTrees = 0;

        foreach (var mesh in geometry.Meshes)
        {
            var meshEdits = edits[mesh.Index];
            var meshVerticesChanged = false;
            for (var vertex = 0; vertex < mesh.Vertices.Count; vertex++)
            {
                if (!NearlyEqual(mesh.Vertices[vertex], meshEdits.Vertices[vertex]))
                {
                    changedVertices++;
                    meshVerticesChanged = true;
                }
                var offset = source.CidbOffset + mesh.Offset + 28 + vertex * 12;
                WriteSingle(output, offset, meshEdits.Vertices[vertex].X);
                WriteSingle(output, offset + 4, meshEdits.Vertices[vertex].Y);
                WriteSingle(output, offset + 8, meshEdits.Vertices[vertex].Z);
            }

            for (var triangle = 0; triangle < mesh.Triangles.Count; triangle++)
            {
                if (mesh.SurfaceIds[triangle] != meshEdits.SurfaceIds[triangle])
                    changedSurfaceIds++;
                WriteUInt16(output, source.CidbOffset + mesh.SurfaceIdsOffset + triangle * 2, meshEdits.SurfaceIds[triangle]);
            }

            // Surface-ID-only edits retain the original cooked hierarchy byte for
            // byte. Refit only a mesh whose vertex positions actually changed.
            if (meshVerticesChanged)
            {
                RefitRtree(output, source.CidbOffset + mesh.RtreeOffset, mesh, meshEdits.Vertices);
                refitTrees++;
            }
        }

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        File.WriteAllBytes(outputPath, output);
        Console.WriteLine($"WRITE {outputPath}");
        Console.WriteLine($"Validated {geometry.Meshes.Count} mesh(es); changed {changedVertices} vertex position(s) and {changedSurfaceIds} triangle surface ID(s); refit {refitTrees} PhysX RTRE tree(s).");
        Console.WriteLine("Topology, face-remap data, tree leaf ranges, and all unknown CIDB bytes were preserved. This command does not add/remove/reorder vertices or faces.");
        return 0;
    }

    private static int TranslateCommand(string[] args)
    {
        if (args.Length != 6)
            return Fail("Usage: translate <source.cidb> <x> <y> <z> <output.cidb>");

        var sourcePath = Path.GetFullPath(args[1]);
        if (!float.TryParse(args[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var x)
            || !float.TryParse(args[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var y)
            || !float.TryParse(args[4], NumberStyles.Float, CultureInfo.InvariantCulture, out var z))
            return Fail("Translation values must be invariant-culture numbers, for example: 0 0 0.5");
        var outputPath = Path.GetFullPath(args[5]);
        if (string.Equals(sourcePath, outputPath, StringComparison.OrdinalIgnoreCase))
            return Fail("Output CIDB must be a new file; the source CIDB is never overwritten.");

        var source = LoadCidbContainer(sourcePath);
        var geometry = DecodeGeometry(Path.GetFileNameWithoutExtension(sourcePath), source.CidbData);
        var output = source.OriginalData.ToArray();
        foreach (var mesh in geometry.Meshes)
        {
            var translated = mesh.Vertices
                .Select(vertex => new Vec3(vertex.X + x, vertex.Y + y, vertex.Z + z))
                .ToArray();
            for (var vertex = 0; vertex < translated.Length; vertex++)
            {
                var offset = source.CidbOffset + mesh.Offset + 28 + vertex * 12;
                WriteSingle(output, offset, translated[vertex].X);
                WriteSingle(output, offset + 4, translated[vertex].Y);
                WriteSingle(output, offset + 8, translated[vertex].Z);
            }
            RefitRtree(output, source.CidbOffset + mesh.RtreeOffset, mesh, translated);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        File.WriteAllBytes(outputPath, output);
        Console.WriteLine($"WRITE {outputPath}");
        Console.WriteLine($"Translated {geometry.Meshes.Count} collision mesh(es) by raw CIDB ({FormatFloat(x)}, {FormatFloat(y)}, {FormatFloat(z)}) and refit all PhysX RTRE tree(s).");
        Console.WriteLine("This is a diagnostic operation: it preserves topology, triangle IDs, face-remap data, and all unknown CIDB bytes.");
        return 0;
    }

    private static int CookBlenderCommand(string[] args)
    {
        if (args.Length != 4)
            return Fail("Usage: cook-blender <template.cidb> <custom-blender.obj> <output.cidb>");

        var templatePath = Path.GetFullPath(args[1]);
        var objPath = Path.GetFullPath(args[2]);
        var outputPath = Path.GetFullPath(args[3]);
        if (string.Equals(templatePath, outputPath, StringComparison.OrdinalIgnoreCase))
            return Fail("Output CIDB must be a new file; the template CIDB is never overwritten.");

        var template = LoadCidbContainer(templatePath);
        var templateGeometry = DecodeGeometry(Path.GetFileNameWithoutExtension(templatePath), template.CidbData);
        if (templateGeometry.Meshes.Count == 0)
            return Fail("The template CIDB contains no NXS/MESH collision data.");
        var source = ParseCookObj(objPath, blenderCoordinates: true);
        var meshes = PartitionCookMesh(source);
        var cooked = BuildCookedCidb(template, templateGeometry, meshes);

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        File.WriteAllBytes(outputPath, cooked);
        Console.WriteLine($"WRITE {outputPath}");
        Console.WriteLine($"Cooked {source.Faces.Count} OBJ triangle(s) into {meshes.Count} CIDB collision mesh(es), with preserved-or-new face remaps and PhysX RTRE trees.");
        Console.WriteLine("Prototype cooker: triangle topology is new; template compatibility fields are retained. Test collision behavior before using it in a production room.");
        return 0;
    }

    private static int CookBlenderLeaf4Command(string[] args)
    {
        if (args.Length != 4)
            return Fail("Usage: cook-blender-leaf4 <template.cidb> <custom-blender.obj> <output.cidb>");
        var templatePath = Path.GetFullPath(args[1]);
        var objPath = Path.GetFullPath(args[2]);
        var outputPath = Path.GetFullPath(args[3]);
        var template = LoadCidbContainer(templatePath);
        var templateGeometry = DecodeGeometry(Path.GetFileNameWithoutExtension(templatePath), template.CidbData);
        var source = ParseCookObj(objPath, blenderCoordinates: true);
        var meshes = PartitionCookMesh(source);
        var cooked = BuildCookedCidb(template, templateGeometry, meshes, maxLeafTriangles: 4);
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        File.WriteAllBytes(outputPath, cooked);
        Console.WriteLine($"WRITE {outputPath}");
        Console.WriteLine("Cooked with four triangles per RTRE leaf as a PhysX hierarchy behavior diagnostic.");
        return 0;
    }

    private static int CookBlenderSpatialCommand(string[] args)
    {
        if (args.Length != 4)
            return Fail("Usage: cook-blender-spatial <template.cidb> <custom-blender.obj> <output.cidb>");
        var templatePath = Path.GetFullPath(args[1]);
        var objPath = Path.GetFullPath(args[2]);
        var outputPath = Path.GetFullPath(args[3]);
        var template = LoadCidbContainer(templatePath);
        var templateGeometry = DecodeGeometry(Path.GetFileNameWithoutExtension(templatePath), template.CidbData);
        var source = ParseCookObj(objPath, blenderCoordinates: true);
        var meshes = PartitionCookMesh(source, spatialOrder: true);
        var cooked = BuildCookedCidb(template, templateGeometry, meshes, maxLeafTriangles: 4);
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        File.WriteAllBytes(outputPath, cooked);
        Console.WriteLine($"WRITE {outputPath}");
        Console.WriteLine("Cooked with spatially permuted triangle records, paired face remaps, and four-triangle RTRE leaves.");
        return 0;
    }

    // verify-cooker: proves (or disproves) that a cooker port reproduces the
    // retail PhysX 3.4 output. Every retail mesh stores mFaceRemap, a full
    // permutation back to the pre-cook triangle order, so the original cook
    // input can be rebuilt exactly. Re-cooking that input must reproduce the
    // stored triangle order, the RTRE pages and the RTRE header byte for byte.
    // The retail archive is therefore a complete regression suite.
    // Diagnostics: CIDB_VERIFY_DUMP=<mesh index> prints retail vs cooked data for
    // that mesh; CIDB_TRACE_SPLIT=1 prints every SAH split decision.
    private static readonly string? VerifyDumpMesh = Environment.GetEnvironmentVariable("CIDB_VERIFY_DUMP");
    private static readonly bool TraceSplits = Environment.GetEnvironmentVariable("CIDB_TRACE_SPLIT") == "1";
    private sealed record CookerCandidate(string Name, Func<CookMesh, (CookMesh Mesh, byte[] Tree)> Cook);
    private sealed record CookerTally(string Candidate, int Meshes, int OrderMatches, int PageMatches, int HeaderMatches, int FullMatches, int FullMatchesLargeMeshes, int LargeMeshes, int Failures);
    private sealed record CookerMeshResult(string File, string Block, int Mesh, int Triangles, IReadOnlyList<string> FullMatches, IReadOnlyList<string> OrderOnlyMatches);
    private sealed record CookerVerifyReport(string Input, int BfresFiles, int Meshes, int LargeMeshThreshold, IReadOnlyList<CookerTally> Candidates, IReadOnlyList<CookerMeshResult> MeshesWithoutFullMatch, IReadOnlyList<string> Failures);

    private static IReadOnlyList<CookerCandidate> AllCookerCandidates()
    {
        var list = new List<CookerCandidate>();
        for (var preset = 0; preset < 15; preset++)
        {
            var p = preset;
            list.Add(new CookerCandidate($"sah{p}", mesh => { var cooked = PreparePhysxMesh(mesh, out var tree, p); return (cooked, tree); }));
        }
        for (var preset = 0; preset < 9; preset++)
        {
            var p = preset;
            list.Add(new CookerCandidate($"quick{p}", mesh => { var cooked = PreparePhysxQuickMesh(mesh, p, out var tree); return (cooked, tree); }));
        }
        return list;
    }

    // verify-edges: recompute the per-triangle active-edge flags (NXS tail
    // u8[T]) with the PhysX 3.4 EdgeListBuilder port and compare with retail.
    private static int VerifyEdgesCommand(string[] args)
    {
        if (args.Length < 2)
            return Fail("Usage: verify-edges <map-directory|file.bfres|file.cidb> [--show N]");
        var input = Path.GetFullPath(args[1]);
        var show = 10;
        for (var index = 2; index < args.Length; index++)
            if (args[index] == "--show" && index + 1 < args.Length) show = int.Parse(args[++index], CultureInfo.InvariantCulture);
        var files = Directory.Exists(input)
            ? Directory.EnumerateFiles(input, "*.bfres", SearchOption.TopDirectoryOnly).OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase).ToArray()
            : new[] { input };
        int meshes = 0, meshOk = 0, tris = 0, triOk = 0, shown = 0;
        var otherBits = 0;
        var confusion = new int[3, 2, 2]; // bit, retail, computed
        foreach (var path in files)
        {
            IReadOnlyList<CidbInput> inputs;
            try { inputs = LoadCidbInputs(path); } catch { continue; }
            foreach (var cidbInput in inputs)
            {
                CidbGeometry geometry;
                try { geometry = DecodeGeometry(cidbInput.Name, cidbInput.Data); } catch { continue; }
                foreach (var mesh in geometry.Meshes)
                {
                    meshes++;
                    var computed = ComputeActiveEdgeFlags(mesh.Vertices, mesh.Triangles);
                    var all = true;
                    for (var t = 0; t < computed.Length; t++)
                    {
                        tris++;
                        var retail = mesh.ExtraFlags[t];
                        if ((retail & ~0x38) != 0) otherBits++;
                        for (var b = 0; b < 3; b++)
                            confusion[b, (retail >> (3 + b)) & 1, (computed[t] >> (3 + b)) & 1]++;
                        if ((retail & 0x38) == computed[t]) { triOk++; continue; }
                        all = false;
                        if (shown++ < show)
                            Console.WriteLine($"MISMATCH {cidbInput.Name} mesh {mesh.Index} tri {t}: retail 0x{retail:X2} computed 0x{computed[t]:X2}");
                    }
                    if (all) meshOk++;
                }
            }
        }
        Console.WriteLine($"Meshes {meshes}: exact {meshOk} ({100.0 * meshOk / Math.Max(meshes, 1):F2}%)");
        Console.WriteLine($"Triangles {tris}: exact {triOk} ({100.0 * triOk / Math.Max(tris, 1):F3}%), retail flags with bits outside 0x38: {otherBits}");
        string[] names = { "edge01 (0x08)", "edge12 (0x10)", "edge20 (0x20)" };
        for (var b = 0; b < 3; b++)
            Console.WriteLine($"  {names[b]}: both0 {confusion[b, 0, 0]}, both1 {confusion[b, 1, 1]}, retail1/computed0 {confusion[b, 1, 0]}, retail0/computed1 {confusion[b, 0, 1]}");
        return meshOk == meshes ? 0 : 1;
    }

    // Port of PhysX 3.4 EdgeListBuilder (createFacesToEdges, createEdgesToFaces,
    // computeActiveEdges, Epsilon 0.1) plus TriangleMeshBuilder::
    // createSharedEdgeData's replication into ETD_CONVEX_EDGE_01/12/20. Runs on
    // the stored (post-R-tree) triangle order, exactly like the cooker.
    private static byte[] ComputeActiveEdgeFlags(IReadOnlyList<Vec3> verts, IReadOnlyList<Triangle> tris)
    {
        const float epsilon = 0.1f;
        var faceCount = tris.Count;
        var result = new byte[faceCount];
        if (faceCount == 0) return result;
        int Ref(int face, int corner) => corner == 0 ? tris[face].A : corner == 1 ? tris[face].B : tris[face].C;
        // Faces per undirected edge, in ascending face order (as FacesByEdges).
        var edgeIndex = new Dictionary<long, int>();
        var edgeRefs = new List<(int R0, int R1)>();
        var edgeFaces = new List<List<int>>();
        var links = new int[faceCount * 3];
        for (var face = 0; face < faceCount; face++)
        {
            for (var j = 0; j < 3; j++)
            {
                int a = Ref(face, j == 2 ? 2 : j), b = Ref(face, j == 2 ? 0 : j + 1);
                int r0 = Math.Min(a, b), r1 = Math.Max(a, b);
                var key = ((long)r0 << 32) | (uint)r1;
                if (!edgeIndex.TryGetValue(key, out var e))
                {
                    e = edgeRefs.Count; edgeIndex.Add(key, e); edgeRefs.Add((r0, r1)); edgeFaces.Add(new List<int>(2));
                }
                // A face that repeats a vertex lists one edge twice; PhysX counts it twice too.
                edgeFaces[e].Add(face);
                links[face * 3 + j] = e;
            }
        }

        static Vec3 Sub(Vec3 a, Vec3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
        static Vec3 Cross(Vec3 a, Vec3 b) => new(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);
        static float Dot(Vec3 a, Vec3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
        static float Magnitude(Vec3 a) => MathF.Sqrt(Dot(a, a));
        // PxTriangle::normal -> PxVec3::normalize (m = magnitude; *this /= m, i.e. multiply by 1/m)
        Vec3 Normal(int face)
        {
            var p0 = verts[Ref(face, 0)];
            var n = Cross(Sub(verts[Ref(face, 1)], p0), Sub(verts[Ref(face, 2)], p0));
            var m = Magnitude(n);
            if (m > 0f) { var f = 1.0f / m; n = new Vec3(n.X * f, n.Y * f, n.Z * f); }
            return n;
        }
        // PxPlane(p0,p1,p2): n = cross.getNormalized() (multiply by PxRecipSqrt(m2)); d = -p0.dot(n)
        float PlaneDistance(int face, Vec3 point)
        {
            var p0 = verts[Ref(face, 0)];
            var n = Cross(Sub(verts[Ref(face, 1)], p0), Sub(verts[Ref(face, 2)], p0));
            var m2 = Dot(n, n);
            n = m2 > 0f ? Scale(n, 1.0f / MathF.Sqrt(m2)) : new Vec3(0, 0, 0);
            var d = -Dot(p0, n);
            return Dot(n, point) + d;
        }
        static Vec3 Scale(Vec3 a, float f) => new(a.X * f, a.Y * f, a.Z * f);
        static float Angle(Vec3 a, Vec3 b) => MathF.Atan2(Magnitude(Cross(a, b)), Dot(a, b));
        int Opposite(int face, int v0, int v1)
        {
            int r0 = Ref(face, 0), r1 = Ref(face, 1), r2 = Ref(face, 2);
            if (v0 == r0) { if (v1 == r1) return r2; if (v1 == r2) return r1; }
            else if (v0 == r1) { if (v1 == r0) return r2; if (v1 == r2) return r0; }
            else if (v0 == r2) { if (v1 == r1) return r0; if (v1 == r0) return r1; }
            return -1;
        }
        bool SameVertexSet(int f, int g)
        {
            for (var j = 0; j < 3; j++)
            {
                var v = Ref(g, j);
                if (v != Ref(f, 0) && v != Ref(f, 1) && v != Ref(f, 2)) return false;
            }
            return true;
        }
        bool ConvexTest(int f0, int f1, int r0, int r1)
        {
            var op = Opposite(f0, r0, r1);
            // PhysX indexes verts[Op] even when Op is invalid; such degenerate
            // faces do not occur in retail data, treat as not-below.
            if (op >= 0 && PlaneDistance(f1, verts[op]) < 0.0f)
                return MathF.Abs(Angle(Normal(f0), Normal(f1))) > epsilon;
            return Dot(Normal(f0), Normal(f1)) < -0.999f;
        }

        var active = new bool[edgeRefs.Count];
        for (var e = 0; e < edgeRefs.Count; e++)
        {
            var faces = edgeFaces[e];
            var (r0, r1) = edgeRefs[e];
            if (faces.Count == 1) { active[e] = true; continue; }
            if (faces.Count == 2) { active[e] = ConvexTest(faces[0], faces[1], r0, r1); continue; }
            var f0 = faces[0];
            var f1 = -1;
            var unique = 1;
            bool doubleSided0 = false, doubleSided1 = false;
            for (var a = 1; a < faces.Count; a++)
            {
                var g = faces[a];
                if (!SameVertexSet(f0, g))
                {
                    if (unique == 2)
                    {
                        if (!SameVertexSet(f1, g)) { unique++; break; }
                        if (Dot(Normal(f1), Normal(g)) < -0.999f) doubleSided1 = true;
                    }
                    else { f1 = g; unique++; }
                }
                else if (Dot(Normal(f0), Normal(g)) < -0.999f) doubleSided0 = true;
            }
            if (unique == 1) active[e] = true;
            else if (unique == 2)
            {
                if (doubleSided0 || doubleSided1)
                    active[e] = MathF.Abs(Angle(Normal(f0), Normal(f1))) > epsilon;
                else
                {
                    var op = Opposite(f0, r0, r1);
                    active[e] = op >= 0 && PlaneDistance(f1, verts[op]) < 0.0f
                        && MathF.Abs(Angle(Normal(f0), Normal(f1))) > epsilon;
                }
            }
            else active[e] = true;
        }
        for (var face = 0; face < faceCount; face++)
        {
            byte flags = 0;
            if (active[links[face * 3 + 0]]) flags |= 0x08; // ETD_CONVEX_EDGE_01
            if (active[links[face * 3 + 1]]) flags |= 0x10; // ETD_CONVEX_EDGE_12
            if (active[links[face * 3 + 2]]) flags |= 0x20; // ETD_CONVEX_EDGE_20
            result[face] = flags;
        }
        return result;
    }

    // ---- Collision preset library -------------------------------------------------
    // Field tables come from the static EXEFS analysis documented in
    // CIDB_FORMAT.md; the preset list is generated from the retail archive.
    private static readonly string[] GroundMaterialNames =
    {
        "GRASS", "GRASS", "TURF", "GRASS", "GRASS", "SAND", "GRAVEL", "DIRT", "DIRT", "WOOD", "WOOD3HO", "STONE",
        "MARBLE", "MARBLE", "STONE", "MUD", "MARBLE", "STONE", "STONE", "CARPET", "MARBLE", "WATER", "SWAMP", "WATER",
        "SWAMP", "MARBLE", "CARPET", "WOOD2CR", "CARPET", "STONE", "STONE", "MUD", "DIRT", "CARPET", "STONE", "CARPET",
        "SAND", "SAND", "DIRT", "DIRT", "GRASS", "DIRT", "DIRT", "DIRT", "SAND", "DIRT", "DIRT", "WATER",
    };
    private static readonly HashSet<int> GameplayConfirmedMaterials = new() { 1, 5, 7, 10, 12, 21, 23, 38, 41 };

    private sealed record PresetField(int Value, string Label, string Effect, string Confidence);
    private sealed record PresetFlag(string Mask, string Label, string Note);
    private sealed record CollisionPreset(
        string Name, string Code, string Category, int Material, string MaterialName, int SurfaceClass, string SurfaceClassLabel,
        string AreaTag, IReadOnlyList<string> Tags, IReadOnlyList<string> Effects, string Confidence,
        long Triangles, double UpFraction, double WallFraction, int Rooms, string ExampleRoom, Vec3? ExamplePoint, bool Recommended = false);
    private sealed record PresetLibrary(
        int Version, string Source, string Note, IReadOnlyList<PresetField> Materials, IReadOnlyList<PresetField> SurfaceClasses,
        IReadOnlyList<PresetFlag> AreaTags, IReadOnlyList<PresetFlag> Flags, IReadOnlyList<CollisionPreset> Presets);

    private static string TitleCase(string name) => name switch
    {
        "WOOD3HO" => "Wood (hollow)", "WOOD2CR" => "Wood (creaky)",
        _ => char.ToUpperInvariant(name[0]) + name[1..].ToLowerInvariant(),
    };

    private static IReadOnlyList<PresetField> PresetMaterialFields() => GroundMaterialNames.Select((name, index) =>
    {
        var effect = $"Footstep/landing sounds SE_FT_*_{name}";
        if (index is 21 or 22) effect += "; walking speed x0.75";
        var confidence = GameplayConfirmedMaterials.Contains(index) ? "confirmed-ingame" : "confirmed-code";
        var label = TitleCase(name);
        if (index == 0) { label = "Default"; effect = "Default material (GRASS sound entry; used on most plain walls)"; confidence = "confirmed-code"; }
        return new PresetField(index, label, effect, confidence);
    }).ToArray();

    private static readonly PresetField[] SurfaceClassFields =
    {
        new(0, "Normal", "Ordinary ground or wall", "confirmed-code"),
        new(1, "Edge (F1)", "Very common on walls/cliff sides and tree-ledge ground; no player-code test found", "unknown"),
        new(2, "F2", "Never used in retail", "unknown"),
        new(3, "Ladder (F3)", "Climbable ladder strip: every F3 face is a 1-1.5 m wide vertical strip facing +/-Z exactly where a ladder is drawn (Field_02M, Bottle Grotto 04D, Angler's Tunnel 07B); no player-code test found yet", "inferred"),
        new(4, "Ladder top (F4)", "Small landing at the top of a field ladder, always paired with an F3 ladder strip (11 field rooms); no player-code test found yet", "inferred"),
        new(5, "Pit", "Fall hazard: player falls and respawns", "confirmed-code"),
        new(6, "Pass-through", "Ground query ignores it and finds the surface below (overlays)", "confirmed-code"),
        new(7, "Deep water", "Dive into water: swim with flippers, otherwise respawn", "confirmed-code"),
        new(8, "Lava", "Dive into lava: damage and respawn", "confirmed-code"),
        new(9, "F9", "Dungeon floors in Face Shrine 04C / Eagle Tower; no player-code test found", "unknown"),
        new(10, "Invisible wall (F10)", "Used by AirWallPiccolo and an Eagle Tower invisible wall", "inferred"),
        new(11, "F11", "Never used in retail", "unknown"), new(12, "F12", "Never used in retail", "unknown"),
        new(13, "F13", "Never used in retail", "unknown"), new(14, "F14", "Never used in retail", "unknown"),
        new(15, "F15", "Never used in retail", "unknown"),
    };

    private static readonly PresetFlag[] AreaTagFields =
    {
        new("0x00000000", "Dungeon", "Dominant in dungeon rooms. Not read by the game; keep the room's convention."),
        new("0x00000400", "Area 0x400", "Not read by the game."),
        new("0x00000800", "Area 0x800", "Not read by the game."),
        new("0x00000C00", "Field", "Dominant in Field_* rooms. Not read by the game."),
        new("0x00001000", "Area 0x1000", "Not read by the game."),
        new("0x00001800", "House", "Dominant in houses. Not read by the game."),
    };

    private static readonly PresetFlag[] FlagFields =
    {
        new("0x00004000", "Tag 0x4000", "Authoring tag; not read by the game."),
        new("0x00040000", "Tag 0x40000", "On AirWallAutojumpD; not read by the player code."),
        new("0x00080000", "Autojump tag", "On AirWallAutojump and many ledges; not read by the player code. Real auto-jumps come from AirWallAutojump actors."),
    };

    private static string AreaLabel(uint code) => (code & 0x1C00) switch
    {
        0 => "Dungeon", 0xC00 => "Field", 0x1800 => "House", var other => $"Area 0x{other:X}",
    };

    private static int ExportPresetsCommand(string[] args)
    {
        if (args.Length < 3)
            return Fail("Usage: export-presets <output.json> <map-directory> [more directories...]");
        var outputPath = Path.GetFullPath(args[1]);
        var stats = new Dictionary<uint, (long Tris, double Area, double Up, double Wall, Dictionary<string, double> Rooms, Vec3? Point, double PointArea)>();
        foreach (var directory in args.Skip(2).Select(Path.GetFullPath))
        {
            foreach (var path in Directory.EnumerateFiles(directory, "*.bfres", SearchOption.TopDirectoryOnly).OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
            {
                IReadOnlyList<CidbInput> inputs;
                try { inputs = LoadCidbInputs(path); } catch { continue; }
                var room = Path.GetFileNameWithoutExtension(path);
                foreach (var input in inputs)
                {
                    CidbGeometry geometry;
                    try { geometry = DecodeGeometry(input.Name, input.Data); } catch { continue; }
                    foreach (var mesh in geometry.Meshes)
                    {
                        for (var t = 0; t < mesh.Triangles.Count; t++)
                        {
                            var slot = mesh.SurfaceIds[t];
                            if (slot >= mesh.MaterialCodes.Count) continue;
                            var code = mesh.MaterialCodes[slot];
                            var tri = mesh.Triangles[t];
                            Vec3 a = mesh.Vertices[tri.A], b = mesh.Vertices[tri.B], c = mesh.Vertices[tri.C];
                            var n = new Vec3((b.Y - a.Y) * (c.Z - a.Z) - (b.Z - a.Z) * (c.Y - a.Y), (b.Z - a.Z) * (c.X - a.X) - (b.X - a.X) * (c.Z - a.Z), (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X));
                            var len = Math.Sqrt((double)n.X * n.X + (double)n.Y * n.Y + (double)n.Z * n.Z);
                            var area = len / 2;
                            var ny = len > 0 ? n.Y / len : 0;
                            if (!stats.TryGetValue(code, out var entry)) entry = (0, 0, 0, 0, new Dictionary<string, double>(), null, 0);
                            entry.Tris++; entry.Area += area;
                            if (ny > 0.7) entry.Up += area; else if (Math.Abs(ny) < 0.3) entry.Wall += area;
                            entry.Rooms[room] = entry.Rooms.GetValueOrDefault(room) + area;
                            if (area > entry.PointArea)
                            {
                                entry.PointArea = area;
                                entry.Point = new Vec3(MathF.Round((a.X + b.X + c.X) / 3, 3), MathF.Round((a.Y + b.Y + c.Y) / 3, 3), MathF.Round((a.Z + b.Z + c.Z) / 3, 3));
                            }
                            stats[code] = entry;
                        }
                    }
                }
            }
        }
        if (stats.Count == 0)
            return Fail("No CIDB triangles found in the given directories.");

        var materials = PresetMaterialFields();
        var presets = new List<CollisionPreset>();
        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (code, entry) in stats.OrderByDescending(item => item.Value.Tris))
        {
            var low6 = (int)(code & 0x3F);
            var f = (int)((code >> 6) & 0xF);
            var up = entry.Area > 0 ? entry.Up / entry.Area : 0;
            var wall = entry.Area > 0 ? entry.Wall / entry.Area : 0;
            var materialName = materials[low6].Label;
            var tags = new List<string>();
            if ((code & 0x4000) != 0) tags.Add("Tag 0x4000");
            if ((code & 0x40000) != 0) tags.Add("Tag 0x40000");
            if ((code & 0x80000) != 0) tags.Add("Autojump tag");
            var unknownHigh = code & ~0xC5FFFu;
            if (unknownHigh != 0) tags.Add($"Bits 0x{unknownHigh:X}");
            var category = f switch
            {
                5 or 7 or 8 => "Hazard",
                6 => "Overlay",
                10 => "Invisible wall",
                3 or 4 => "Ladder",
                9 => "Special",
                _ when low6 is 21 or 22 or 23 or 24 or 47 => "Water",
                _ when wall > 0.6 => "Wall",
                _ => "Ground",
            };
            var effects = new List<string> { materials[low6].Effect };
            if (f != 0) effects.Add(SurfaceClassFields[f].Effect);
            var confidence = f is 0 or 5 or 6 or 7 or 8 ? (GameplayConfirmedMaterials.Contains(low6) ? "confirmed-ingame" : "confirmed-code") : SurfaceClassFields[f].Confidence;
            var shape = category == "Wall" ? " wall" : "";
            var classPart = f is 0 ? "" : $" [F{f}]";
            var classLabel = SurfaceClassFields[f].Label;
            var shortClass = classLabel.Contains(" (F") ? classLabel[..classLabel.IndexOf(" (F", StringComparison.Ordinal)] : classLabel;
            var head = f is 3 or 4 or 5 or 6 or 7 or 8 or 10
                ? $"{shortClass} ({materialName})" + (f is 3 or 4 or 10 ? $" [F{f}]" : "")
                : $"{materialName}{shape}{classPart}";
            var tail = $" · {AreaLabel(code)}" + (tags.Count > 0 ? $" · {string.Join(", ", tags)}" : "");
            var name = head + tail;
            if (!usedNames.Add(name))
            {
                name = $"{head} #{low6}{tail}";
                if (!usedNames.Add(name)) { name = $"{head}{tail} · 0x{code:X}"; usedNames.Add(name); }
            }
            var exampleRoom = entry.Rooms.OrderByDescending(item => item.Value).First().Key;
            presets.Add(new CollisionPreset(name, $"0x{code:X8}", category, low6, materialName, f, SurfaceClassFields[f].Label,
                AreaLabel(code), tags, effects, confidence, entry.Tris, Math.Round(up, 3), Math.Round(wall, 3), entry.Rooms.Count, exampleRoom, entry.Point));
        }

        // Recommended = the most used code for each (category, material, area).
        var recommended = presets.GroupBy(p => (p.Category, p.MaterialName, p.AreaTag)).Select(g => g.OrderByDescending(p => p.Triangles).First().Code).ToHashSet();
        presets = presets.Select(p => p with { Recommended = recommended.Contains(p.Code) && p.Triangles >= 20 }).ToList();
        var library = new PresetLibrary(1,
            "Generated by CidbTool export-presets from the retail romfs map/actor archives.",
            "Code = bits 0-5 material (footstep + speed), bits 6-9 surface class F, bits 10-12 area tag, higher bits authoring tags. Only bits 0-9 are read by the player code. Auto-jump ledges and similar are level actors (AirWallAutojump*), not collision codes.",
            materials, SurfaceClassFields, AreaTagFields, FlagFields, presets);
        var options = new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        File.WriteAllText(outputPath, JsonSerializer.Serialize(library, options));
        Console.WriteLine($"WRITE {outputPath} ({presets.Count} presets)");
        return 0;
    }

    private static int VerifyCookerCommand(string[] args)
    {
        if (args.Length < 2)
            return Fail("Usage: verify-cooker <map-directory|file.bfres|file.cidb> [report.json] [--only sah8,quick4] [--limit N]");
        var input = Path.GetFullPath(args[1]);
        string? reportPath = null;
        string[]? only = null;
        var limit = int.MaxValue;
        for (var index = 2; index < args.Length; index++)
        {
            if (args[index] == "--only" && index + 1 < args.Length) only = args[++index].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            else if (args[index] == "--limit" && index + 1 < args.Length) limit = int.Parse(args[++index], CultureInfo.InvariantCulture);
            else reportPath = Path.GetFullPath(args[index]);
        }
        var files = Directory.Exists(input)
            ? Directory.EnumerateFiles(input, "*.bfres", SearchOption.TopDirectoryOnly).OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase).Take(limit).ToArray()
            : new[] { input };
        var candidates = AllCookerCandidates().Where(candidate => only == null || only.Contains(candidate.Name, StringComparer.OrdinalIgnoreCase)).ToArray();
        if (candidates.Length == 0)
            return Fail("No cooker candidates selected.");

        const int largeMesh = 16; // tiny meshes match many presets by construction
        var counters = candidates.ToDictionary(c => c.Name, _ => new int[8]); // meshes, order, pages, header, full, fullLarge, large, failures
        var unmatched = new List<CookerMeshResult>();
        var failures = new List<string>();
        var meshTotal = 0;

        foreach (var path in files)
        {
            IReadOnlyList<CidbInput> inputs;
            try { inputs = LoadCidbInputs(path); }
            catch (Exception exception)
            {
                if (!exception.Message.Contains("No CIDB", StringComparison.OrdinalIgnoreCase)) failures.Add($"{Path.GetFileName(path)}: {exception.Message}");
                continue;
            }
            foreach (var cidbInput in inputs)
            {
                CidbGeometry geometry;
                try { geometry = DecodeGeometry(cidbInput.Name, cidbInput.Data); }
                catch (Exception exception) { failures.Add($"{cidbInput.Name}: {exception.Message}"); continue; }
                foreach (var mesh in geometry.Meshes)
                {
                    meshTotal++;
                    var count = mesh.Triangles.Count;
                    var pageCount = (int)ReadUInt32BigEndian(cidbInput.Data, mesh.RtreeOffset + 0x58);
                    var retailHeader = cidbInput.Data.AsSpan(mesh.RtreeOffset + 8, 0x58).ToArray(); // skip magic+version
                    var retailPages = cidbInput.Data.AsSpan(mesh.RtreeOffset + 0x60, pageCount * RtreePageBytes).ToArray();
                    var source = NormalizePhysxInput(new CookMesh(mesh.Vertices, mesh.Triangles, mesh.SurfaceIds, mesh.ExtraFlags, mesh.FaceRemaps));
                    var full = new List<string>();
                    var orderOnly = new List<string>();
                    foreach (var candidate in candidates)
                    {
                        var c = counters[candidate.Name];
                        c[0]++;
                        if (count >= largeMesh) c[6]++;
                        try
                        {
                            var (cooked, tree) = candidate.Cook(source);
                            var orderOk = cooked.FaceRemaps.SequenceEqual(mesh.FaceRemaps);
                            var pagesOk = tree.Length == 0x60 + retailPages.Length && tree.AsSpan(0x60).SequenceEqual(retailPages);
                            var headerOk = tree.Length >= 0x60 && tree.AsSpan(8, 0x58).SequenceEqual(retailHeader);
                            if (VerifyDumpMesh is { } dumpMesh && dumpMesh == mesh.Index.ToString(CultureInfo.InvariantCulture))
                            {
                                Console.WriteLine($"DUMP {cidbInput.Name} mesh {mesh.Index} {candidate.Name} tris={count} pages={pageCount}");
                                Console.WriteLine("  retail order: " + string.Join(",", mesh.FaceRemaps));
                                Console.WriteLine("  cooked order: " + string.Join(",", cooked.FaceRemaps));
                                var retailFull = cidbInput.Data.AsSpan(mesh.RtreeOffset, 0x60 + retailPages.Length).ToArray();
                                for (var w = 0; w < Math.Max(retailFull.Length, tree.Length) / 4; w++)
                                {
                                    var a = w * 4 < retailFull.Length ? ReadUInt32BigEndian(retailFull, w * 4) : 0xDEADDEADu;
                                    var b = w * 4 < tree.Length ? ReadUInt32BigEndian(tree, w * 4) : 0xDEADDEADu;
                                    if (a != b) Console.WriteLine($"  word +0x{w * 4:X3}: retail {a:X8} ({BitConverter.UInt32BitsToSingle(a)})  cooked {b:X8} ({BitConverter.UInt32BitsToSingle(b)})");
                                }
                                for (var t = 0; t < count; t++)
                                {
                                    var tri = source.Triangles[t];
                                    Console.WriteLine($"  in{t}: {mesh.Vertices[tri.A]} {mesh.Vertices[tri.B]} {mesh.Vertices[tri.C]}");
                                }
                            }
                            if (orderOk) c[1]++;
                            if (pagesOk) c[2]++;
                            if (headerOk) c[3]++;
                            if (orderOk && pagesOk && headerOk)
                            {
                                c[4]++;
                                if (count >= largeMesh) c[5]++;
                                full.Add(candidate.Name);
                            }
                            else if (orderOk) orderOnly.Add(candidate.Name);
                        }
                        catch (Exception exception)
                        {
                            c[7]++;
                            if (failures.Count < 200) failures.Add($"{cidbInput.Name} mesh {mesh.Index} {candidate.Name}: {exception.Message}");
                        }
                    }
                    if (full.Count == 0)
                        unmatched.Add(new CookerMeshResult(Path.GetFileName(path), cidbInput.Name, mesh.Index, count, full, orderOnly));
                }
            }
        }

        var tallies = candidates.Select(candidate =>
        {
            var c = counters[candidate.Name];
            return new CookerTally(candidate.Name, c[0], c[1], c[2], c[3], c[4], c[5], c[6], c[7]);
        }).OrderByDescending(t => t.FullMatches).ThenByDescending(t => t.OrderMatches).ToArray();

        Console.WriteLine($"verify-cooker: {files.Length} file(s), {meshTotal} retail mesh(es); large = {largeMesh}+ triangles");
        Console.WriteLine($"{"candidate",-9} {"order",7} {"pages",7} {"header",7} {"FULL",7} {"FULL(large)",12} {"errors",7}");
        foreach (var t in tallies)
            Console.WriteLine($"{t.Candidate,-9} {t.OrderMatches,7} {t.PageMatches,7} {t.HeaderMatches,7} {t.FullMatches,7} {$"{t.FullMatchesLargeMeshes}/{t.LargeMeshes}",12} {t.Failures,7}");
        Console.WriteLine($"meshes with no fully matching candidate: {unmatched.Count}");
        if (reportPath != null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
            File.WriteAllText(reportPath, JsonSerializer.Serialize(new CookerVerifyReport(input, files.Length, meshTotal, largeMesh, tallies, unmatched, failures), JsonOptions));
            Console.WriteLine($"WRITE {reportPath}");
        }
        foreach (var failure in failures.Take(10)) Console.WriteLine($"FAIL {failure}");
        return 0;
    }

    // verify-recook: end-to-end check of the cook-blender-physx container path.
    // Each retail CIDB is decoded and fed back through BuildCookedCidbPhysx in
    // its stored triangle order; the whole output must equal the original bytes.
    private static int VerifyRecookCommand(string[] args)
    {
        if (args.Length is < 2 or > 3)
            return Fail("Usage: verify-recook <map-directory|file.bfres|file.cidb> [--layout]");
        var input = Path.GetFullPath(args[1]);
        var layout = args.Length == 3 && args[2] == "--layout";
        var files = Directory.Exists(input)
            ? Directory.EnumerateFiles(input, "*.bfres", SearchOption.TopDirectoryOnly).OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase).ToArray()
            : new[] { input };
        int containers = 0, identical = 0;
        var mismatches = new List<string>();
        foreach (var path in files)
        {
            IReadOnlyList<CidbInput> inputs;
            try { inputs = LoadCidbInputs(path); }
            catch (Exception exception)
            {
                if (!exception.Message.Contains("No CIDB", StringComparison.OrdinalIgnoreCase)) mismatches.Add($"{Path.GetFileName(path)}: {exception.Message}");
                continue;
            }
            foreach (var cidbInput in inputs)
            {
                containers++;
                try
                {
                    var template = new CidbContainer(cidbInput.Data, 0, cidbInput.Data);
                    var geometry = DecodeGeometry(cidbInput.Name, cidbInput.Data);
                    var meshes = geometry.Meshes.Select(mesh => new CookMesh(mesh.Vertices, mesh.Triangles, mesh.SurfaceIds, mesh.ExtraFlags, mesh.FaceRemaps)).ToArray();
                    var rebuilt = layout ? BuildCookedCidbPhysxAtOriginalOffsets(template, geometry, meshes) : BuildCookedCidbPhysx(template, geometry, meshes);
                    if (Environment.GetEnvironmentVariable("CIDB_RECOOK_OUT") is { Length: > 0 } recookDir)
                        File.WriteAllBytes(Path.Combine(recookDir, cidbInput.Name + ".recooked.cidb"), rebuilt);
                    // The retail container ends after its last section; a BFRES slice may
                    // continue with padding or the next external file, which is not ours.
                    var retailEnd = ReadSectionOffsets(cidbInput.Data).Select(offset => offset + SectionLength(cidbInput.Data, offset)).DefaultIfEmpty(cidbInput.Data.Length).Max();
                    var compareLength = layout ? cidbInput.Data.Length : retailEnd;
                    if (rebuilt.Length >= compareLength && rebuilt.Length <= cidbInput.Data.Length
                        && rebuilt.AsSpan(0, compareLength).SequenceEqual(cidbInput.Data.AsSpan(0, compareLength)))
                    { identical++; continue; }
                    var first = 0;
                    while (first < Math.Min(rebuilt.Length, cidbInput.Data.Length) && rebuilt[first] == cidbInput.Data[first]) first++;
                    mismatches.Add($"{cidbInput.Name}: size {rebuilt.Length} vs {cidbInput.Data.Length}, first difference at 0x{first:X}");
                }
                catch (Exception exception) { mismatches.Add($"{cidbInput.Name}: {exception.Message}"); if (Environment.GetEnvironmentVariable("CIDB_STACK") == "1") Console.WriteLine(exception); }
            }
        }
        Console.WriteLine($"verify-recook{(layout ? " --layout" : "")}: {identical}/{containers} CIDB containers rebuilt byte-identical");
        foreach (var line in mismatches.Take(15)) Console.WriteLine($"  {line}");
        if (mismatches.Count > 15) Console.WriteLine($"  ... {mismatches.Count - 15} more");
        return mismatches.Count == 0 ? 0 : 1;
    }

    private static int CookBlenderPhysxCommand(string[] args)
    {
        if (args.Length is < 4 or > 5)
            return Fail("Usage: cook-blender-physx <template.cidb> <custom-blender.obj> <output.cidb> [tradeoff-index-0-to-14]");
        var tradeoff = 8; // PhysX default meshSizePerformanceTradeoff 0.55; verified against all retail meshes
        if (args.Length == 5 && (!int.TryParse(args[4], NumberStyles.None, CultureInfo.InvariantCulture, out tradeoff) || tradeoff is < 0 or > 14))
            return Fail("SAH-cooker tradeoff index must be an integer from 0 through 14.");
        var templatePath = Path.GetFullPath(args[1]);
        var objPath = Path.GetFullPath(args[2]);
        var outputPath = Path.GetFullPath(args[3]);
        var template = LoadCidbContainer(templatePath);
        var templateGeometry = DecodeGeometry(Path.GetFileNameWithoutExtension(templatePath), template.CidbData);
        var source = ParseCookObj(objPath, blenderCoordinates: true);
        var meshes = PartitionCookMesh(source);
        var cooked = BuildCookedCidbPhysx(template, templateGeometry, meshes, tradeoff, source.Primitives);
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        File.WriteAllBytes(outputPath, cooked);
        Console.WriteLine($"WRITE {outputPath}");
        Console.WriteLine($"Cooked with the PhysX BVH33-style SAH permutation and RTRE hierarchy (tradeoff preset {tradeoff}).");
        return 0;
    }

    // The game's executable contains both PhysX's RTreeCooking.cpp and the
    // RTreeTriangleMesh runtime. That cooker has two branches: the SAH branch
    // above and this older/faster QuickSelect branch. Keep this separate so we
    // can identify the branch used by Nintendo's cook rather than treating one
    // tree shape as universal.
    private static int CookBlenderPhysxQuickCommand(string[] args)
    {
        if (args.Length is < 4 or > 5)
            return Fail("Usage: cook-blender-physx-quick <template.cidb> <custom-blender.obj> <output.cidb> [tradeoff-index-0-to-8]");
        var tradeoff = 0;
        if (args.Length == 5 && (!int.TryParse(args[4], NumberStyles.None, CultureInfo.InvariantCulture, out tradeoff) || tradeoff is < 0 or > 8))
            return Fail("Quick-cooker tradeoff index must be an integer from 0 through 8.");
        var templatePath = Path.GetFullPath(args[1]);
        var objPath = Path.GetFullPath(args[2]);
        var outputPath = Path.GetFullPath(args[3]);
        var template = LoadCidbContainer(templatePath);
        var templateGeometry = DecodeGeometry(Path.GetFileNameWithoutExtension(templatePath), template.CidbData);
        var source = ParseCookObj(objPath, blenderCoordinates: true);
        var meshes = PartitionCookMesh(source);
        var cooked = BuildCookedCidbPhysxQuick(template, templateGeometry, meshes, tradeoff);
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        File.WriteAllBytes(outputPath, cooked);
        Console.WriteLine($"WRITE {outputPath}");
        Console.WriteLine($"Cooked with PhysX's QuickSelect RTree branch (tradeoff preset {tradeoff}).");
        return 0;
    }

    private static int CookBlenderPhysxLayoutCommand(string[] args)
    {
        if (args.Length != 4)
            return Fail("Usage: cook-blender-physx-layout <template.cidb> <custom-blender.obj> <output.cidb>");
        var templatePath = Path.GetFullPath(args[1]);
        var objPath = Path.GetFullPath(args[2]);
        var outputPath = Path.GetFullPath(args[3]);
        var template = LoadCidbContainer(templatePath);
        var templateGeometry = DecodeGeometry(Path.GetFileNameWithoutExtension(templatePath), template.CidbData);
        var source = ParseCookObj(objPath, blenderCoordinates: true);
        var meshes = PartitionCookMesh(source);
        var cooked = BuildCookedCidbPhysxAtOriginalOffsets(template, templateGeometry, meshes, source.Primitives);
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        File.WriteAllBytes(outputPath, cooked);
        Console.WriteLine($"WRITE {outputPath}");
        Console.WriteLine("Cooked with the PhysX SAH path while retaining every template CIDB section descriptor and offset.");
        return 0;
    }

    private static int CookBlenderTemplateTreeCommand(string[] args)
    {
        if (args.Length != 4)
            return Fail("Usage: cook-blender-template-tree <template.cidb> <custom-blender.obj> <output.cidb>");
        var templatePath = Path.GetFullPath(args[1]);
        var objPath = Path.GetFullPath(args[2]);
        var outputPath = Path.GetFullPath(args[3]);
        if (string.Equals(templatePath, outputPath, StringComparison.OrdinalIgnoreCase))
            return Fail("Output CIDB must be a new file; the template CIDB is never overwritten.");

        var template = LoadCidbContainer(templatePath);
        var templateGeometry = DecodeGeometry(Path.GetFileNameWithoutExtension(templatePath), template.CidbData);
        var source = ParseCookObj(objPath, blenderCoordinates: true);
        var meshes = PartitionCookMesh(source);
        var cooked = BuildCidbWithTemplateTree(template, templateGeometry, meshes);
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        File.WriteAllBytes(outputPath, cooked);
        Console.WriteLine($"WRITE {outputPath}");
        Console.WriteLine("Wrote custom vertices, faces, surface IDs, and edge flags while retaining the donor RTRE page layout, pointers, and face-remap block. Each collision mesh must retain its donor vertex and triangle count.");
        return 0;
    }

    private static int RestoreMeshCommand(string[] args)
    {
        if (args.Length != 5)
            return Fail("Usage: restore-mesh <original.cidb> <cooked.cidb> <one-based-mesh-index> <output.cidb>");
        if (!int.TryParse(args[3], NumberStyles.None, CultureInfo.InvariantCulture, out var meshIndex) || meshIndex < 1)
            return Fail("Mesh index must be a positive one-based integer.");

        var original = LoadCidbContainer(Path.GetFullPath(args[1]));
        var cooked = LoadCidbContainer(Path.GetFullPath(args[2]));
        var outputPath = Path.GetFullPath(args[4]);
        var originalSections = ReadRawCidbSections(original.CidbData);
        var cookedSections = ReadRawCidbSections(cooked.CidbData);
        if (originalSections.Count != cookedSections.Count || meshIndex > cookedSections.Count)
            return Fail("Original and cooked CIDB files must have the same mesh count and contain the selected mesh.");

        cookedSections[meshIndex - 1] = originalSections[meshIndex - 1];
        var rebuilt = BuildCidbContainer(cooked, cookedSections);
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        File.WriteAllBytes(outputPath, rebuilt);
        Console.WriteLine($"WRITE {outputPath}");
        Console.WriteLine($"Restored original mesh {meshIndex} byte-for-byte inside the cooked CIDB. This is a focused behavior diagnostic, not a topology-editing workflow.");
        return 0;
    }

    private static int CookBlenderLayoutCommand(string[] args)
    {
        if (args.Length != 4)
            return Fail("Usage: cook-blender-layout <template.cidb> <custom-blender.obj> <output.cidb>");

        var templatePath = Path.GetFullPath(args[1]);
        var objPath = Path.GetFullPath(args[2]);
        var outputPath = Path.GetFullPath(args[3]);
        if (string.Equals(templatePath, outputPath, StringComparison.OrdinalIgnoreCase))
            return Fail("Output CIDB must be a new file; the template CIDB is never overwritten.");

        var template = LoadCidbContainer(templatePath);
        var templateGeometry = DecodeGeometry(Path.GetFileNameWithoutExtension(templatePath), template.CidbData);
        var source = ParseCookObj(objPath, blenderCoordinates: true);
        var meshes = PartitionCookMesh(source);
        var cooked = BuildCookedCidbAtOriginalOffsets(template, templateGeometry, meshes);
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        File.WriteAllBytes(outputPath, cooked);
        Console.WriteLine($"WRITE {outputPath}");
        Console.WriteLine($"Cooked {source.Faces.Count} OBJ triangle(s) while preserving every original CIDB section offset and descriptor. This diagnostic requires the cooked payloads to fit the template's section slots.");
        return 0;
    }

    private static int CreateTopologySmokeTestCommand(string[] args)
    {
        if (args.Length != 3)
            return Fail("Usage: create-topology-smoke-test <source.cidb> <output.cidb>");
        var sourcePath = Path.GetFullPath(args[1]);
        var outputPath = Path.GetFullPath(args[2]);
        if (string.Equals(sourcePath, outputPath, StringComparison.OrdinalIgnoreCase))
            return Fail("Output CIDB must be a new file; the source CIDB is never overwritten.");

        var source = LoadCidbContainer(sourcePath);
        var geometry = DecodeGeometry(Path.GetFileNameWithoutExtension(sourcePath), source.CidbData);
        if (geometry.Meshes.Count == 0)
            return Fail("The source CIDB contains no collision meshes.");
        var meshes = geometry.Meshes.Select(mesh => new CookMesh(
            mesh.Vertices.ToArray(),
            mesh.Triangles.ToArray(),
            mesh.SurfaceIds.ToArray(),
            mesh.ExtraFlags.ToArray(),
            Enumerable.Range(0, mesh.Triangles.Count).Select(value => checked((ushort)value)).ToArray())).ToArray();

        // A minimal, geometry-invariant topology change: split one near-horizontal,
        // walkable triangle into three coplanar triangles. It preserves the chosen
        // triangle's CIDB material-table slot and active-edge byte and touches no
        // other map data. This is a loader/cooker regression test, not an authoring
        // workflow or a claim that the altered face is visually distinguishable.
        var targetMeshIndex = -1;
        var targetTriangleIndex = -1;
        for (var meshIndex = 0; meshIndex < geometry.Meshes.Count && targetMeshIndex < 0; meshIndex++)
        {
            var mesh = geometry.Meshes[meshIndex];
            for (var triangleIndex = 0; triangleIndex < mesh.Triangles.Count; triangleIndex++)
            {
                var triangle = mesh.Triangles[triangleIndex];
                var a = mesh.Vertices[triangle.A];
                var b = mesh.Vertices[triangle.B];
                var c = mesh.Vertices[triangle.C];
                var ux = b.X - a.X; var uy = b.Y - a.Y; var uz = b.Z - a.Z;
                var vx = c.X - a.X; var vy = c.Y - a.Y; var vz = c.Z - a.Z;
                var normalY = uz * vx - ux * vz;
                var length = MathF.Sqrt(
                    (uy * vz - uz * vy) * (uy * vz - uz * vy)
                    + normalY * normalY
                    + (ux * vy - uy * vx) * (ux * vy - uy * vx));
                if (length > 0.00001f && MathF.Abs(normalY / length) > 0.90f)
                {
                    targetMeshIndex = meshIndex;
                    targetTriangleIndex = triangleIndex;
                    break;
                }
            }
        }
        if (targetMeshIndex < 0)
            return Fail("No near-horizontal collision triangle was found for the topology smoke test.");

        var original = geometry.Meshes[targetMeshIndex];
        var target = original.Triangles[targetTriangleIndex];
        var targetSlot = original.SurfaceIds[targetTriangleIndex];
        if (targetSlot >= original.MaterialCodes.Count)
            return Fail("The selected topology-test triangle refers to a material slot outside its serialized contact-code table.");
        var vertices = original.Vertices.ToList();
        var aVertex = vertices[target.A]; var bVertex = vertices[target.B]; var cVertex = vertices[target.C];
        var centerIndex = vertices.Count;
        vertices.Add(new Vec3((aVertex.X + bVertex.X + cVertex.X) / 3f, (aVertex.Y + bVertex.Y + cVertex.Y) / 3f, (aVertex.Z + bVertex.Z + cVertex.Z) / 3f));
        var triangles = original.Triangles.ToList();
        triangles[targetTriangleIndex] = new Triangle(target.A, target.B, centerIndex);
        triangles.Insert(targetTriangleIndex + 1, new Triangle(target.B, target.C, centerIndex));
        triangles.Insert(targetTriangleIndex + 2, new Triangle(target.C, target.A, centerIndex));
        var slots = original.SurfaceIds.ToList();
        slots[targetTriangleIndex] = targetSlot;
        slots.Insert(targetTriangleIndex + 1, targetSlot);
        slots.Insert(targetTriangleIndex + 2, targetSlot);
        var edgeFlags = original.ExtraFlags.ToList();
        var targetEdgeFlags = original.ExtraFlags[targetTriangleIndex];
        edgeFlags[targetTriangleIndex] = targetEdgeFlags;
        edgeFlags.Insert(targetTriangleIndex + 1, targetEdgeFlags);
        edgeFlags.Insert(targetTriangleIndex + 2, targetEdgeFlags);
        meshes[targetMeshIndex] = new CookMesh(vertices, triangles, slots, edgeFlags,
            Enumerable.Range(0, triangles.Count).Select(value => checked((ushort)value)).ToArray());

        var output = BuildCookedCidb(source, geometry, meshes);
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        File.WriteAllBytes(outputPath, output);
        Console.WriteLine($"WRITE {outputPath}");
        Console.WriteLine($"Topology smoke test: mesh {targetMeshIndex + 1}, triangle {targetTriangleIndex} was split into three coplanar triangles; mesh now has {vertices.Count} vertices and {triangles.Count} triangles. It retains slot {targetSlot} -> full contact code 0x{original.MaterialCodes[targetSlot]:X8}.");
        return 0;
    }

    private static int CreateBarrierSmokeTestCommand(string[] args)
    {
        if (args.Length != 3)
            return Fail("Usage: create-barrier-smoke-test <source.cidb> <output.cidb>");
        var sourcePath = Path.GetFullPath(args[1]);
        var outputPath = Path.GetFullPath(args[2]);
        if (string.Equals(sourcePath, outputPath, StringComparison.OrdinalIgnoreCase))
            return Fail("Output CIDB must be a new file; the source CIDB is never overwritten.");

        var source = LoadCidbContainer(sourcePath);
        var geometry = DecodeGeometry(Path.GetFileNameWithoutExtension(sourcePath), source.CidbData);
        if (geometry.Meshes.Count == 0)
            return Fail("The source CIDB contains no collision meshes.");
        var meshes = geometry.Meshes.Select(mesh => new CookMesh(
            mesh.Vertices.ToArray(), mesh.Triangles.ToArray(), mesh.SurfaceIds.ToArray(),
            mesh.ExtraFlags.ToArray(), Enumerable.Range(0, mesh.Triangles.Count).Select(value => checked((ushort)value)).ToArray())).ToArray();

        // TownWell-specific physical smoke test.  This creates a double-sided,
        // 1.0-unit-wide, 1.5-unit-high invisible wall over dry mesh-1 floor at
        // X=-5.25, Z=0.25..1.25: immediately left of the central pool. Its four
        // new triangles use that mesh's slot 0 (dry cave floor profile). It is
        // intentionally not a general authoring feature; it makes a new collision
        // surface unambiguously testable before relying on arbitrary Blender input.
        var targetMeshIndex = 0;
        var mesh = geometry.Meshes[targetMeshIndex];
        const ushort slot = 0;
        if (slot >= mesh.MaterialCodes.Count)
            return Fail("TownWell barrier test requires mesh 1 material slot 0.");
        var vertices = mesh.Vertices.ToList();
        var baseIndex = vertices.Count;
        vertices.Add(new Vec3(-5.25f, 0.00f, 0.25f));
        vertices.Add(new Vec3(-5.25f, 0.00f, 1.25f));
        vertices.Add(new Vec3(-5.25f, 1.50f, 0.25f));
        vertices.Add(new Vec3(-5.25f, 1.50f, 1.25f));
        var triangles = mesh.Triangles.ToList();
        // Both windings are emitted so the barrier does not depend on triangle
        // sidedness in the game-side collision query.
        triangles.Add(new Triangle(baseIndex, baseIndex + 1, baseIndex + 2));
        triangles.Add(new Triangle(baseIndex + 1, baseIndex + 3, baseIndex + 2));
        triangles.Add(new Triangle(baseIndex + 2, baseIndex + 1, baseIndex));
        triangles.Add(new Triangle(baseIndex + 2, baseIndex + 3, baseIndex + 1));
        var slots = mesh.SurfaceIds.ToList();
        slots.Add(slot); slots.Add(slot); slots.Add(slot); slots.Add(slot);
        var edgeFlags = mesh.ExtraFlags.ToList();
        edgeFlags.Add(0); edgeFlags.Add(0); edgeFlags.Add(0); edgeFlags.Add(0);
        meshes[targetMeshIndex] = new CookMesh(vertices, triangles, slots, edgeFlags,
            Enumerable.Range(0, triangles.Count).Select(value => checked((ushort)value)).ToArray());

        var output = BuildCookedCidb(source, geometry, meshes);
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        File.WriteAllBytes(outputPath, output);
        Console.WriteLine($"WRITE {outputPath}");
        Console.WriteLine($"Barrier smoke test: mesh 1 adds a double-sided invisible wall at X=-5.25, Z=0.25..1.25, Y=0..1.5; {vertices.Count} vertices and {triangles.Count} triangles, slot 0 -> 0x{mesh.MaterialCodes[slot]:X8}.");
        return 0;
    }

    private static int RebuildRtreeLayoutCommand(string[] args)
    {
        if (args.Length != 3)
            return Fail("Usage: rebuild-rtree-layout <source.cidb> <output.cidb>");
        var sourcePath = Path.GetFullPath(args[1]);
        var outputPath = Path.GetFullPath(args[2]);
        if (string.Equals(sourcePath, outputPath, StringComparison.OrdinalIgnoreCase))
            return Fail("Output CIDB must be a new file; the source CIDB is never overwritten.");

        var source = LoadCidbContainer(sourcePath);
        var geometry = DecodeGeometry(Path.GetFileNameWithoutExtension(sourcePath), source.CidbData);
        var cidb = source.CidbData.ToArray();
        for (var index = 0; index < geometry.Meshes.Count; index++)
        {
            var mesh = geometry.Meshes[index];
            var sectionOffset = mesh.Offset - 0x2C;
            var (nextOffset, clearEnd) = SectionSlot(source.CidbData, sectionOffset);
            var originalPages = ReadUInt32BigEndian(source.CidbData, mesh.RtreeOffset + 0x58);
            var originalTailOffset = checked(mesh.RtreeOffset + 0x60 + (int)originalPages * RtreePageBytes);
            var tailLength = checked(32 + mesh.Triangles.Count);
            var tree = BuildRtree(mesh.Vertices, mesh.Triangles);
            var prefixLength = mesh.RtreeOffset - mesh.Offset;
            var payloadLength = checked(prefixLength + tree.Length + tailLength);
            var materialCodeByteCount = checked(mesh.MaterialCodes.Count * 4);
            var sectionLength = checked(0x2C + payloadLength + materialCodeByteCount);
            if (sectionLength > nextOffset - sectionOffset)
                throw new InvalidDataException($"Rebuilt RTRE for mesh {index + 1} does not fit the original CIDB section slot.");

            WriteUInt32(cidb, sectionOffset + 4, (uint)payloadLength);
            WriteUInt32(cidb, sectionOffset + 8, (uint)materialCodeByteCount);
            var newRtreeOffset = mesh.Offset + prefixLength;
            Buffer.BlockCopy(tree, 0, cidb, newRtreeOffset, tree.Length);
            Buffer.BlockCopy(source.CidbData, originalTailOffset, cidb, newRtreeOffset + tree.Length, tailLength);
            var materialCodesOffset = newRtreeOffset + tree.Length + tailLength;
            for (var codeIndex = 0; codeIndex < mesh.MaterialCodes.Count; codeIndex++)
                WriteUInt32(cidb, materialCodesOffset + codeIndex * 4, mesh.MaterialCodes[codeIndex]);
            if (clearEnd > sectionOffset + sectionLength)
                Array.Clear(cidb, sectionOffset + sectionLength, clearEnd - (sectionOffset + sectionLength));
        }

        var output = source.CidbOffset == 0 ? cidb : source.OriginalData.ToArray();
        if (source.CidbOffset != 0)
            Buffer.BlockCopy(cidb, 0, output, source.CidbOffset, cidb.Length);
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        File.WriteAllBytes(outputPath, output);
        Console.WriteLine($"WRITE {outputPath}");
        Console.WriteLine("Rebuilt only the PhysX RTRE trees; all raw NXS geometry and per-triangle metadata remain byte-for-byte vanilla.");
        return 0;
    }

    private static CidbReport Inspect(string inputPath)
    {
        if (!File.Exists(inputPath))
            throw new FileNotFoundException("BFRES file not found.", inputPath);

        var data = File.ReadAllBytes(inputPath);
        if (data.Length < BfresHeaderSize || !data.AsSpan(0, 4).SequenceEqual(BfresMagic))
            throw new InvalidDataException("Not a BFRES file (expected FRES magic).");

        var declaredSize = ReadUInt32(data, 0x1C);
        var relocationOffset = ReadUInt32(data, RelocationOffsetField);
        if (relocationOffset == 0 || relocationOffset > data.Length)
            throw new InvalidDataException($"BFRES relocation offset 0x{relocationOffset:X} is outside this file.");

        var starts = FindAll(data, CidbMagic).Where(offset => offset < relocationOffset).ToArray();
        var blocks = new List<CidbBlock>();
        for (var index = 0; index < starts.Length; index++)
        {
            var start = starts[index];
            // CIDB data is located before BFRES relocation metadata. If a future file carries
            // more than one CIDB, the next CIDB is the only safe boundary we can prove.
            var end = index + 1 < starts.Length ? starts[index + 1] : (int)relocationOffset;
            if (end <= start + 0x10)
                throw new InvalidDataException($"CIDB at 0x{start:X} is truncated.");

            var headerSize = ReadUInt32(data, start + 0x04);
            var versionOrFlags = ReadUInt32(data, start + 0x08);
            var sectionCount = ReadUInt32(data, start + 0x0C);
            var sectionOffsets = new List<uint>();
            if (sectionCount <= 64 && start + 0x10L + sectionCount * 4 <= end)
            {
                for (var sectionIndex = 0; sectionIndex < sectionCount; sectionIndex++)
                    sectionOffsets.Add(ReadUInt32(data, start + 0x10 + sectionIndex * 4));
            }

            blocks.Add(new CidbBlock(
                start,
                end,
                end - start,
                headerSize,
                versionOrFlags,
                sectionCount,
                sectionOffsets,
                Convert.ToHexString(SHA256.HashData(data.AsSpan(start, end - start)))));
        }

        return new CidbReport(
            inputPath,
            data.Length,
            declaredSize,
            declaredSize == data.Length,
            relocationOffset,
            blocks);
    }

    private static void WriteExtraction(string inputPath, string outputDirectory, CidbReport report)
    {
        if (report.Blocks.Count == 0)
        {
            Console.WriteLine($"SKIP  {Path.GetFileName(inputPath)} (no CIDB block)");
            return;
        }

        Directory.CreateDirectory(outputDirectory);
        var data = File.ReadAllBytes(inputPath);
        var baseName = Path.GetFileNameWithoutExtension(inputPath);
        for (var index = 0; index < report.Blocks.Count; index++)
        {
            var block = report.Blocks[index];
            var suffix = report.Blocks.Count == 1 ? string.Empty : $"_{index + 1:D2}";
            var cidbPath = Path.Combine(outputDirectory, $"{baseName}{suffix}.cidb");
            var manifestPath = Path.Combine(outputDirectory, $"{baseName}{suffix}.cidb.json");
            File.WriteAllBytes(cidbPath, data[block.StartOffset..block.EndOffset]);
            File.WriteAllText(manifestPath, JsonSerializer.Serialize(new ExtractionManifest(report, block, cidbPath), JsonOptions));
            Console.WriteLine($"WRITE {Path.GetFileName(cidbPath)}  0x{block.StartOffset:X}-0x{block.EndOffset:X} ({block.Length:N0} bytes)");
        }
    }

    private static IReadOnlyList<CidbInput> LoadCidbInputs(string inputPath)
    {
        var data = File.ReadAllBytes(inputPath);
        var baseName = Path.GetFileNameWithoutExtension(inputPath);
        if (data.AsSpan(0, Math.Min(data.Length, 4)).SequenceEqual(CidbMagic))
            return new[] { new CidbInput(baseName, data) };

        // Switch Toolbox exports BFRES ExternalFile data with a four-byte wrapper and
        // omits trailing alignment padding. The CIDB payload itself starts at +0x04.
        var cidbOffsets = FindAll(data, CidbMagic).ToArray();
        if (cidbOffsets.Length == 1 && cidbOffsets[0] > 0 && cidbOffsets[0] <= 0x10)
            return new[] { new CidbInput(baseName, data[cidbOffsets[0]..]) };

        if (!data.AsSpan(0, Math.Min(data.Length, 4)).SequenceEqual(BfresMagic))
            throw new InvalidDataException("Expected either a CIDB file or a BFRES file.");

        var report = Inspect(inputPath);
        return report.Blocks.Select((block, index) =>
        {
            var suffix = report.Blocks.Count == 1 ? string.Empty : $"_{index + 1:D2}";
            return new CidbInput(baseName + suffix, data[block.StartOffset..block.EndOffset]);
        }).ToArray();
    }

    private static CidbContainer LoadCidbContainer(string inputPath)
    {
        if (!File.Exists(inputPath))
            throw new FileNotFoundException("CIDB file not found.", inputPath);
        var data = File.ReadAllBytes(inputPath);
        if (data.AsSpan(0, Math.Min(data.Length, 4)).SequenceEqual(CidbMagic))
            return new CidbContainer(data, 0, data);

        var cidbOffsets = FindAll(data, CidbMagic).ToArray();
        if (cidbOffsets.Length == 1 && cidbOffsets[0] > 0 && cidbOffsets[0] <= 0x10)
            return new CidbContainer(data, cidbOffsets[0], data[cidbOffsets[0]..]);

        throw new InvalidDataException("rebuild-surface accepts a raw CIDB or a Switch Toolbox-exported CIDB, not a BFRES file.");
    }

    private static byte[] ToToolboxCidb(CidbContainer source, byte[] containerData)
    {
        if (containerData.Length < source.CidbOffset + 4
            || !containerData.AsSpan(source.CidbOffset, 4).SequenceEqual(CidbMagic))
            throw new InvalidDataException("Expected a CIDB container at the source wrapper offset.");
        var payloadLength = containerData.Length - source.CidbOffset;
        var toolbox = new byte[payloadLength + 4];
        WriteUInt32(toolbox, 0, 4);
        Buffer.BlockCopy(containerData, source.CidbOffset, toolbox, 4, payloadLength);
        return toolbox;
    }

    private static CidbGeometry DecodeGeometry(string name, byte[] cidb)
    {
        if (cidb.Length < 4 || !cidb.AsSpan(0, 4).SequenceEqual(CidbMagic))
            throw new InvalidDataException("Not a CIDB container (expected CIDB magic).");

        var meshOffsets = FindAll(cidb, NxsMeshMagic).ToArray();
        var meshes = new List<CollisionMesh>();
        for (var meshIndex = 0; meshIndex < meshOffsets.Length; meshIndex++)
        {
            var offset = meshOffsets[meshIndex];
            RequireRange(cidb, offset, 28, $"NXS MESH header at 0x{offset:X}");
            // Every observed CIDB NXS payload is immediately preceded by its
            // 0x2c-byte outer section descriptor.  Its +0x04 field is the NXS
            // payload length and +0x08 is the byte length of a little-endian
            // u32 contact/behaviour table stored directly after that payload.
            // This table is what the runtime indexes after reading PhysX's
            // per-triangle u16 mMaterialIndices array.
            var descriptorOffset = checked(offset - 0x2C);
            RequireRange(cidb, descriptorOffset, 0x2C, $"CIDB descriptor for NXS MESH at 0x{offset:X}");
            var payloadLength = ReadUInt32(cidb, descriptorOffset + 4);
            var materialCodeByteCount = ReadUInt32(cidb, descriptorOffset + 8);
            if (payloadLength == 0 || payloadLength < 28)
                throw new InvalidDataException($"CIDB descriptor at 0x{descriptorOffset:X} has an invalid NXS payload length.");
            if ((materialCodeByteCount & 3) != 0)
                throw new InvalidDataException($"CIDB descriptor at 0x{descriptorOffset:X} has a non-u32 material-code table length.");
            var materialCodesOffset = checked(offset + (int)payloadLength);
            RequireRange(cidb, materialCodesOffset, checked((int)materialCodeByteCount), $"CIDB material-code table for NXS MESH at 0x{offset:X}");
            var materialCodes = new List<uint>((int)materialCodeByteCount / 4);
            for (var codeIndex = 0; codeIndex < materialCodeByteCount / 4; codeIndex++)
                materialCodes.Add(ReadUInt32(cidb, materialCodesOffset + codeIndex * 4));
            var serialFlags = ReadUInt32(cidb, offset + 16);
            var vertexCount = ReadUInt32(cidb, offset + 20);
            var triangleCount = ReadUInt32(cidb, offset + 24);
            var vertexByteCount = checked((int)vertexCount * 12);
            // PhysX serial flags describe the stored index width. Retail room
            // meshes also make the expected choice from their vertex count,
            // but the flag is the authoritative format field.
            var indexWidth = GetMeshIndexWidth(serialFlags, vertexCount);
            var indexByteCount = checked((int)triangleCount * 3 * indexWidth);
            var verticesOffset = checked(offset + 28);
            var indicesOffset = checked(verticesOffset + vertexByteCount);
            var surfaceIdsOffset = checked(indicesOffset + indexByteCount);
            var surfaceIdsByteCount = checked((int)triangleCount * 2);
            RequireRange(cidb, verticesOffset, checked(vertexByteCount + indexByteCount + surfaceIdsByteCount + 4), $"NXS MESH payload at 0x{offset:X}");

            var vertices = new List<Vec3>((int)vertexCount);
            for (var vertex = 0; vertex < vertexCount; vertex++)
            {
                var cursor = verticesOffset + vertex * 12;
                vertices.Add(new Vec3(
                    ReadSingle(cidb, cursor),
                    ReadSingle(cidb, cursor + 4),
                    ReadSingle(cidb, cursor + 8)));
            }

            var triangles = new List<Triangle>((int)triangleCount);
            for (var triangle = 0; triangle < triangleCount; triangle++)
            {
                var cursor = indicesOffset + triangle * 3 * indexWidth;
                var a = indexWidth == 1 ? cidb[cursor] : ReadUInt16(cidb, cursor);
                var b = indexWidth == 1 ? cidb[cursor + 1] : ReadUInt16(cidb, cursor + indexWidth);
                var c = indexWidth == 1 ? cidb[cursor + 2] : ReadUInt16(cidb, cursor + indexWidth * 2);
                if (a >= vertexCount || b >= vertexCount || c >= vertexCount)
                    throw new InvalidDataException($"NXS MESH at 0x{offset:X} has an out-of-range triangle index.");
                triangles.Add(new Triangle(a, b, c));
            }

            // PhysX mMaterialIndices: one u16 material-table index per triangle.
            // The enclosing CIDB descriptor identifies the following per-mesh
            // u32 contact-code table that resolves these indices at runtime.
            var surfaceIds = new List<ushort>((int)triangleCount);
            for (var triangle = 0; triangle < triangleCount; triangle++)
                surfaceIds.Add(ReadUInt16(cidb, surfaceIdsOffset + triangle * 2));

            // A variable-width face-remap prelude follows the surface IDs. Its first
            // word is preserved for research, but its encoding has variants. RTRE is
            // a stable marker and the first one before the next NXS/MESH is the safe
            // boundary for all observed room files.
            var rtreePreludeOffset = surfaceIdsOffset + surfaceIdsByteCount;
            var rtreePreludeFirstWord = ReadUInt32(cidb, rtreePreludeOffset);
            var nextMeshOffset = meshIndex + 1 < meshOffsets.Length ? meshOffsets[meshIndex + 1] : cidb.Length;
            var rtreeOffset = FindFirst(cidb, RtreeMagic, rtreePreludeOffset + 4, nextMeshOffset);
            if (rtreeOffset < 0)
                throw new InvalidDataException($"NXS MESH at 0x{offset:X} has no RTRE marker before the next mesh boundary.");

            var rtreePages = ReadUInt32BigEndian(cidb, rtreeOffset + 0x58);
            var tailOffset = checked(rtreeOffset + 0x60 + (int)rtreePages * RtreePageBytes);
            RequireRange(cidb, tailOffset, checked(32 + (int)triangleCount), $"NXS MESH tail at 0x{offset:X}");
            if (ReadUInt32(cidb, tailOffset + 28) != triangleCount)
                throw new InvalidDataException($"NXS MESH at 0x{offset:X} has a tail triangle count that does not match its header.");
            var extraFlags = new List<byte>((int)triangleCount);
            for (var triangle = 0; triangle < triangleCount; triangle++)
                extraFlags.Add(cidb[tailOffset + 32 + triangle]);
            var faceRemaps = new List<ushort>((int)triangleCount);
            // PhysX GuSerialize storeIndices: the prelude word is the largest remap
            // value and the element width follows that value, not the triangle
            // index width. The largest value exceeds T-1 when PhysX's mesh cleaner
            // dropped degenerate/duplicate faces before cooking.
            var remapWidth = RemapWidth(rtreePreludeFirstWord);
            if (remapWidth <= 2 && rtreeOffset - rtreePreludeOffset == triangleCount * remapWidth + 4)
            {
                for (var triangle = 0; triangle < triangleCount; triangle++)
                    faceRemaps.Add(remapWidth == 1
                        ? cidb[rtreePreludeOffset + 4 + triangle]
                        : ReadUInt16(cidb, rtreePreludeOffset + 4 + triangle * remapWidth));
            }
            else
            {
                for (var triangle = 0; triangle < triangleCount; triangle++)
                    faceRemaps.Add(checked((ushort)triangle));
            }

            meshes.Add(new CollisionMesh(
                meshIndex + 1,
                offset,
                descriptorOffset,
                payloadLength,
                materialCodesOffset,
                materialCodes,
                ReadUInt32(cidb, offset + 8),
                ReadUInt32(cidb, offset + 12),
                serialFlags,
                vertices,
                triangles,
                surfaceIds,
                surfaceIdsOffset,
                rtreePreludeFirstWord,
                rtreeOffset - rtreePreludeOffset,
                rtreeOffset,
                ReadUInt32(cidb, tailOffset),
                ReadSingle(cidb, tailOffset),
                extraFlags,
                faceRemaps));
        }

        return new CidbGeometry(name, cidb.Length, meshes);
    }

    private static void WriteGeometry(string outputDirectory, CidbGeometry geometry)
    {
        var objPath = Path.Combine(outputDirectory, $"{geometry.Name}.collision.obj");
        var blenderObjPath = Path.Combine(outputDirectory, $"{geometry.Name}.collision.blender.obj");
        var materialPath = Path.Combine(outputDirectory, $"{geometry.Name}.collision.mtl");
        var blenderMaterialPath = Path.Combine(outputDirectory, $"{geometry.Name}.collision.blender.mtl");
        var reportPath = Path.Combine(outputDirectory, $"{geometry.Name}.collision.json");
        var topSvgPath = Path.Combine(outputDirectory, $"{geometry.Name}.collision.top.svg");
        var elevationSvgPath = Path.Combine(outputDirectory, $"{geometry.Name}.collision.elevation.svg");
        var materials = geometry.Meshes.SelectMany(mesh => mesh.SurfaceIds.Select((surfaceId, index) => new MaterialKey(surfaceId, mesh.ExtraFlags[index], mesh.FaceRemaps[index])))
            .Distinct().OrderBy(item => item.SurfaceId).ThenBy(item => item.ExtraFlag).ThenBy(item => item.FaceRemap).ToArray();
        WriteMtl(materialPath, materials);
        WriteMtl(blenderMaterialPath, materials);
        WriteObj(objPath, geometry, vertex => vertex, "Raw CIDB coordinates.", Path.GetFileName(materialPath));
        // Switch Toolbox's Collada exporter uses this axis conversion. It is the same
        // transform verified by overlaying TownWell_01A collision onto its DAE in Blender.
        WriteObj(blenderObjPath, geometry, ToBlenderDaeSpace, "Blender companion: rotate raw CIDB coordinates -90 degrees about X.", Path.GetFileName(blenderMaterialPath));

        var report = new GeometryManifest(
            geometry.Name,
            geometry.CidbSize,
            geometry.Meshes.Count,
            geometry.Meshes.Sum(mesh => mesh.Vertices.Count),
            geometry.Meshes.Sum(mesh => mesh.Triangles.Count),
            geometry.Meshes.Select(mesh => new MeshManifest(
                mesh.Index,
                mesh.Offset,
                mesh.DescriptorOffset,
                mesh.PayloadLength,
                mesh.MaterialCodesOffset,
                mesh.MaterialCodes,
                mesh.HeaderField0,
                mesh.HeaderField1,
                mesh.SerializationFlags,
                mesh.Vertices.Count,
                mesh.Triangles.Count,
                Bounds(mesh.Vertices),
                mesh.SurfaceIds,
                mesh.RtreePreludeFirstWord,
                mesh.RtreePreludeLength,
                mesh.RtreeOffset,
                mesh.GeomEpsilon)).ToArray());
        File.WriteAllText(reportPath, JsonSerializer.Serialize(report, JsonOptions));
        WriteProjectionSvg(topSvgPath, geometry, Projection.Top);
        WriteProjectionSvg(elevationSvgPath, geometry, Projection.Elevation);
        Console.WriteLine($"WRITE {Path.GetFileName(objPath)} + {Path.GetFileName(blenderObjPath)}  {report.TotalVertices:N0} vertices, {report.TotalTriangles:N0} triangles in {report.MeshCount} mesh(es), {materials.Length} Blender visualization material group(s); OBJ + top/elevation SVG");
    }

    private static void WriteObj(string outputPath, CidbGeometry geometry, Func<Vec3, Vec3> transform, string coordinateNote, string materialFileName)
    {
        using var writer = new StreamWriter(outputPath, false, new UTF8Encoding(false));
        writer.WriteLine("# Link's Awakening CIDB collision mesh (read-only decode)");
        writer.WriteLine("# " + coordinateNote);
        writer.WriteLine("# Blender-only visualization materials: each name encodes a CIDB triangle material-table index, edge flag, and optional face remap.");
        writer.WriteLine("mtllib " + materialFileName);
        var vertexBase = 1;
        foreach (var mesh in geometry.Meshes)
        {
            writer.WriteLine($"o collision_mesh_{mesh.Index:D3}_at_{mesh.Offset:X}");
            foreach (var vertex in mesh.Vertices)
            {
                var transformed = transform(vertex);
                writer.WriteLine($"v {FormatFloat(transformed.X)} {FormatFloat(transformed.Y)} {FormatFloat(transformed.Z)}");
            }
            MaterialKey? currentMaterial = null;
            for (var triangleIndex = 0; triangleIndex < mesh.Triangles.Count; triangleIndex++)
            {
                var triangle = mesh.Triangles[triangleIndex];
                var material = new MaterialKey(mesh.SurfaceIds[triangleIndex], mesh.ExtraFlags[triangleIndex], mesh.FaceRemaps[triangleIndex]);
                if (currentMaterial != material)
                {
                    writer.WriteLine($"g collision_mesh_{mesh.Index:D3}_surface_{material.SurfaceId}_extra_{material.ExtraFlag}");
                    writer.WriteLine($"usemtl {MaterialName(material)}");
                    currentMaterial = material;
                }
                writer.WriteLine($"f {vertexBase + triangle.A} {vertexBase + triangle.B} {vertexBase + triangle.C}");
            }
            vertexBase += mesh.Vertices.Count;
        }
    }

    private static void WriteMtl(string outputPath, IReadOnlyList<MaterialKey> materials)
    {
        using var writer = new StreamWriter(outputPath, false, new UTF8Encoding(false));
        writer.WriteLine("# CIDB surface IDs. Colors distinguish raw IDs only; they do not imply gameplay meaning.");
        foreach (var material in materials)
        {
            var color = MtlColors[material.SurfaceId % MtlColors.Length];
            writer.WriteLine($"newmtl {MaterialName(material)}");
            writer.WriteLine("Kd " + color);
            writer.WriteLine("d 0.55");
            writer.WriteLine("illum 2");
            writer.WriteLine();
        }
    }

    private static void WriteSurfaceLayers(string outputDirectory, CidbGeometry geometry)
    {
        var layerDirectory = Path.Combine(outputDirectory, $"{geometry.Name}_surface_layers");
        Directory.CreateDirectory(layerDirectory);
        var layers = geometry.Meshes.SelectMany(mesh => mesh.SurfaceIds).Distinct().OrderBy(id => id).ToArray();
        var mtlFileName = $"{geometry.Name}.surface-layers.mtl";
        WriteSurfaceLayerMtl(Path.Combine(layerDirectory, mtlFileName), layers);
        var manifestLayers = new List<SurfaceLayerManifest>();
        foreach (var surfaceId in layers)
        {
            var fileName = $"{geometry.Name}.surface_{surfaceId:D2}.blender.obj";
            var outputPath = Path.Combine(layerDirectory, fileName);
            var meshCounts = WriteSurfaceLayerObj(outputPath, geometry, surfaceId, mtlFileName);
            WriteSurfaceLayerSvg(Path.Combine(layerDirectory, $"{geometry.Name}.surface_{surfaceId:D2}.top.svg"), geometry, surfaceId, Projection.Top);
            WriteSurfaceLayerSvg(Path.Combine(layerDirectory, $"{geometry.Name}.surface_{surfaceId:D2}.elevation.svg"), geometry, surfaceId, Projection.Elevation);
            manifestLayers.Add(new SurfaceLayerManifest(surfaceId, meshCounts.Sum(item => item.TriangleCount), meshCounts));
        }
        var manifest = new SurfaceLayersManifest(geometry.Name, "Blender DAE coordinate space: (x, y, z) CIDB -> (x, z, -y). These are preview-only isolated surface layers; do not use them as bake input.", manifestLayers);
        File.WriteAllText(Path.Combine(layerDirectory, $"{geometry.Name}.surface-layers.json"), JsonSerializer.Serialize(manifest, JsonOptions));
        Console.WriteLine($"WRITE {Path.GetFileName(layerDirectory)}  {layers.Length} Blender surface layer OBJ(s) + manifest");
    }

    private static IReadOnlyList<SurfaceLayerMeshManifest> WriteSurfaceLayerObj(string outputPath, CidbGeometry geometry, ushort surfaceId, string materialFileName)
    {
        var meshReports = new List<SurfaceLayerMeshManifest>();
        using var writer = new StreamWriter(outputPath, false, new UTF8Encoding(false));
        writer.WriteLine("# Link's Awakening CIDB isolated surface preview (read-only export)");
        writer.WriteLine("# Blender coordinate space: (x, y, z) CIDB -> (x, z, -y).");
        writer.WriteLine($"# Raw CIDB surface ID: {surfaceId}. This OBJ is for visualization only; it is not a bake input.");
        writer.WriteLine("mtllib " + materialFileName);
        var vertexBase = 1;
        foreach (var mesh in geometry.Meshes)
        {
            var triangleIndices = Enumerable.Range(0, mesh.Triangles.Count)
                .Where(index => mesh.SurfaceIds[index] == surfaceId)
                .ToArray();
            if (triangleIndices.Length == 0)
                continue;

            var usedVertices = triangleIndices
                .SelectMany(index => new[] { mesh.Triangles[index].A, mesh.Triangles[index].B, mesh.Triangles[index].C })
                .Distinct()
                .OrderBy(index => index)
                .ToArray();
            var remap = usedVertices.Select((oldIndex, newIndex) => new { oldIndex, newIndex })
                .ToDictionary(item => item.oldIndex, item => item.newIndex);
            writer.WriteLine($"o cidb_surface_{surfaceId}_mesh_{mesh.Index:D3}_at_{mesh.Offset:X}");
            foreach (var oldIndex in usedVertices)
            {
                var vertex = ToBlenderDaeSpace(mesh.Vertices[oldIndex]);
                writer.WriteLine($"v {FormatFloat(vertex.X)} {FormatFloat(vertex.Y)} {FormatFloat(vertex.Z)}");
            }
            writer.WriteLine($"g cidb_surface_{surfaceId}_mesh_{mesh.Index:D3}");
            writer.WriteLine($"usemtl CIDB_SURFACE_{surfaceId}");
            foreach (var triangleIndex in triangleIndices)
            {
                var triangle = mesh.Triangles[triangleIndex];
                writer.WriteLine($"f {vertexBase + remap[triangle.A]} {vertexBase + remap[triangle.B]} {vertexBase + remap[triangle.C]}");
            }
            meshReports.Add(new SurfaceLayerMeshManifest(mesh.Index, mesh.Offset, usedVertices.Length, triangleIndices.Length));
            vertexBase += usedVertices.Length;
        }
        return meshReports;
    }

    private static void WriteSurfaceLayerMtl(string outputPath, IReadOnlyList<ushort> surfaceIds)
    {
        using var writer = new StreamWriter(outputPath, false, new UTF8Encoding(false));
        writer.WriteLine("# Preview colours distinguish raw CIDB IDs only; they do not establish gameplay semantics.");
        foreach (var surfaceId in surfaceIds)
        {
            writer.WriteLine($"newmtl CIDB_SURFACE_{surfaceId}");
            writer.WriteLine("Kd " + MtlColors[surfaceId % MtlColors.Length]);
            writer.WriteLine("d 0.70");
            writer.WriteLine("illum 2");
            writer.WriteLine();
        }
    }

    private static IReadOnlyList<FloorPatchCandidate> FindLargestFloorPatch(CidbGeometry geometry)
    {
        // A controlled surface test must not move geometry or alter wall/object
        // collision. Select only existing surface-0 faces whose raw normal
        // faces +Y, then select the dominant coplanar height. This identifies
        // BananaHouse_01A's open main floor (45 triangles) without relying on
        // any rendered texture or actor data.
        var candidates = new List<FloorPatchCandidate>();
        foreach (var mesh in geometry.Meshes)
        {
            for (var triangleIndex = 0; triangleIndex < mesh.Triangles.Count; triangleIndex++)
            {
                if (mesh.SurfaceIds[triangleIndex] != 0)
                    continue;
                var triangle = mesh.Triangles[triangleIndex];
                var a = mesh.Vertices[triangle.A];
                var b = mesh.Vertices[triangle.B];
                var c = mesh.Vertices[triangle.C];
                var ux = b.X - a.X; var uy = b.Y - a.Y; var uz = b.Z - a.Z;
                var vx = c.X - a.X; var vy = c.Y - a.Y; var vz = c.Z - a.Z;
                var normalY = uz * vx - ux * vz;
                var length = MathF.Sqrt(
                    (uy * vz - uz * vy) * (uy * vz - uz * vy)
                    + normalY * normalY
                    + (ux * vy - uy * vx) * (ux * vy - uy * vx));
                if (length <= 0.00001f || normalY / length < 0.90f)
                    continue;
                var center = new Vec3((a.X + b.X + c.X) / 3f, (a.Y + b.Y + c.Y) / 3f, (a.Z + b.Z + c.Z) / 3f);
                candidates.Add(new FloorPatchCandidate(mesh.Index, mesh.SurfaceIdsOffset, triangleIndex, center));
            }
        }

        return candidates
            .GroupBy(item => MathF.Round(item.Center.Y, 2))
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key)
            .FirstOrDefault()?
            .OrderBy(item => item.MeshIndex)
            .ThenBy(item => item.TriangleIndex)
            .ToArray() ?? Array.Empty<FloorPatchCandidate>();
    }

    private static void WriteSurfaceLayerSvg(string outputPath, CidbGeometry geometry, ushort surfaceId, Projection projection)
    {
        var allVertices = geometry.Meshes.SelectMany(mesh => mesh.Vertices).ToArray();
        if (allVertices.Length == 0)
            return;
        var horizontalMin = allVertices.Min(vertex => vertex.X);
        var horizontalMax = allVertices.Max(vertex => vertex.X);
        var verticalMin = projection == Projection.Top
            ? allVertices.Min(vertex => vertex.Z)
            : allVertices.Min(vertex => vertex.Y);
        var verticalMax = projection == Projection.Top
            ? allVertices.Max(vertex => vertex.Z)
            : allVertices.Max(vertex => vertex.Y);
        var horizontalRange = Math.Max(horizontalMax - horizontalMin, 0.01f);
        var verticalRange = Math.Max(verticalMax - verticalMin, 0.01f);
        var scale = 900f / Math.Max(horizontalRange, verticalRange);
        var usedWidth = horizontalRange * scale;
        var usedHeight = verticalRange * scale;
        var left = 50f + (900f - usedWidth) / 2f;
        var bottom = 950f - (900f - usedHeight) / 2f;
        var axisLabel = projection == Projection.Top ? "Top projection: X / Z" : "Elevation projection: X / Y";
        var color = SvgColors[surfaceId % SvgColors.Length];
        var triangles = geometry.Meshes.Sum(mesh => mesh.SurfaceIds.Count(id => id == surfaceId));

        using var writer = new StreamWriter(outputPath, false, new UTF8Encoding(false));
        writer.WriteLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
        writer.WriteLine("<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"1000\" height=\"1000\" viewBox=\"0 0 1000 1000\">");
        writer.WriteLine("  <rect width=\"1000\" height=\"1000\" fill=\"#101217\"/>");
        writer.WriteLine($"  <text x=\"20\" y=\"30\" fill=\"#eeeeee\" font-family=\"sans-serif\" font-size=\"18\">{geometry.Name} — raw CIDB surface {surfaceId} — {axisLabel}</text>");
        writer.WriteLine("  <g fill=\"#6d7480\" fill-opacity=\"0.12\" stroke=\"#6d7480\" stroke-width=\"0.4\">");
        foreach (var mesh in geometry.Meshes)
        foreach (var triangle in mesh.Triangles)
        {
            var a = Project(mesh.Vertices[triangle.A], projection, horizontalMin, verticalMin, scale, left, bottom);
            var b = Project(mesh.Vertices[triangle.B], projection, horizontalMin, verticalMin, scale, left, bottom);
            var c = Project(mesh.Vertices[triangle.C], projection, horizontalMin, verticalMin, scale, left, bottom);
            writer.WriteLine($"    <polygon points=\"{FormatFloat(a.X)},{FormatFloat(a.Y)} {FormatFloat(b.X)},{FormatFloat(b.Y)} {FormatFloat(c.X)},{FormatFloat(c.Y)}\"/>");
        }
        writer.WriteLine("  </g>");
        writer.WriteLine($"  <g fill=\"{color}\" fill-opacity=\"0.80\" stroke=\"#ffffff\" stroke-width=\"1.2\">");
        foreach (var mesh in geometry.Meshes)
        for (var index = 0; index < mesh.Triangles.Count; index++)
        {
            if (mesh.SurfaceIds[index] != surfaceId)
                continue;
            var triangle = mesh.Triangles[index];
            var a = Project(mesh.Vertices[triangle.A], projection, horizontalMin, verticalMin, scale, left, bottom);
            var b = Project(mesh.Vertices[triangle.B], projection, horizontalMin, verticalMin, scale, left, bottom);
            var c = Project(mesh.Vertices[triangle.C], projection, horizontalMin, verticalMin, scale, left, bottom);
            writer.WriteLine($"    <polygon points=\"{FormatFloat(a.X)},{FormatFloat(a.Y)} {FormatFloat(b.X)},{FormatFloat(b.Y)} {FormatFloat(c.X)},{FormatFloat(c.Y)}\"/>");
        }
        writer.WriteLine("  </g>");
        writer.WriteLine($"  <text x=\"20\" y=\"985\" fill=\"#bbbbbb\" font-family=\"sans-serif\" font-size=\"14\">Highlighted: {triangles} triangle(s). Gray is all CIDB collision for room-position context.</text>");
        writer.WriteLine("</svg>");
    }

    private static string MaterialName(MaterialKey material) => material.FaceRemap.HasValue
        ? $"cidb_surface_{material.SurfaceId}_extra_{material.ExtraFlag}_remap_{material.FaceRemap.Value}"
        : $"cidb_surface_{material.SurfaceId}_extra_{material.ExtraFlag}";

    private static ObjDocument ParseObj(string inputPath)
    {
        if (!File.Exists(inputPath))
            throw new FileNotFoundException("OBJ file not found.", inputPath);
        var vertices = new List<Vec3>();
        var objects = new List<ObjObject>();
        ObjObject? currentObject = null;
        var currentMaterial = string.Empty;
        var isBlenderCompanion = false;

        foreach (var rawLine in File.ReadLines(inputPath))
        {
            var line = rawLine.Trim();
            if (line.StartsWith("# Blender companion", StringComparison.Ordinal))
            {
                isBlenderCompanion = true;
                continue;
            }
            if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal))
                continue;
            if (line.StartsWith("o ", StringComparison.Ordinal))
            {
                currentObject = new ObjObject(line[2..].Trim(), new List<ObjFace>());
                objects.Add(currentObject);
                continue;
            }
            if (line.StartsWith("v ", StringComparison.Ordinal))
            {
                var parts = line[2..].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length != 3 || !float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var x)
                    || !float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var y)
                    || !float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var z))
                    throw new InvalidDataException($"Invalid OBJ vertex: {rawLine}");
                vertices.Add(new Vec3(x, y, z));
                continue;
            }
            if (line.StartsWith("usemtl ", StringComparison.Ordinal))
            {
                currentMaterial = line[7..].Trim();
                continue;
            }
            if (line.StartsWith("f ", StringComparison.Ordinal))
            {
                if (currentObject == null)
                    throw new InvalidDataException("OBJ face appears before an object declaration.");
                var parts = line[2..].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length != 3)
                    throw new InvalidDataException("Only triangulated OBJ faces are supported by rebuild-surface.");
            var material = ParseMaterialKey(currentMaterial);
            currentObject.Faces.Add(new ObjFace(ParseObjVertexIndex(parts[0]), ParseObjVertexIndex(parts[1]), ParseObjVertexIndex(parts[2]), material.SurfaceId, material.ExtraFlag));
            }
        }
        return new ObjDocument(vertices, objects, isBlenderCompanion);
    }

    private static CookInput ParseCookObj(string inputPath, bool blenderCoordinates)
    {
        if (!File.Exists(inputPath))
            throw new FileNotFoundException("OBJ file not found.", inputPath);
        var vertices = new List<Vec3>();
        var faces = new List<CookFace>();
        var sourceGroupStarts = new Dictionary<int, int>();
        var currentMaterial = new MaterialKey(0, 0);
        var currentSourceGroup = 0;
        List<PrimitiveSpec>? primitives = null;
        foreach (var rawLine in File.ReadLines(inputPath))
        {
            var line = rawLine.Trim();
            if (line.StartsWith("#las_primitives", StringComparison.Ordinal))
            {
                primitives ??= new List<PrimitiveSpec>();
                continue;
            }
            if (line.StartsWith("#las_primitive ", StringComparison.Ordinal))
            {
                (primitives ??= new List<PrimitiveSpec>()).Add(ParsePrimitiveLine(line));
                continue;
            }
            if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal))
                continue;
            if (line.StartsWith("v ", StringComparison.Ordinal))
            {
                var parts = line[2..].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 3 || !float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var x)
                    || !float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var y)
                    || !float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var z))
                    throw new InvalidDataException($"Invalid OBJ vertex: {rawLine}");
                var input = new Vec3(x, y, z);
                vertices.Add(blenderCoordinates ? FromBlenderDaeSpace(input) : input);
                continue;
            }
            if (line.StartsWith("usemtl ", StringComparison.Ordinal))
            {
                var name = line[7..].Trim();
                currentMaterial = name.StartsWith("cidb_surface_", StringComparison.Ordinal)
                    || name.StartsWith("cidb_contact_", StringComparison.Ordinal)
                    ? ParseMaterialKey(name) : new MaterialKey(0, 0);
                continue;
            }
            if (line.StartsWith("o ", StringComparison.Ordinal))
            {
                // A decoded OBJ emits one object per original NXS/MESH block.
                // Keep that boundary when possible: face-remap values are local to
                // their source mesh and are not necessarily ordered numerically.
                currentSourceGroup++;
                // Our decoded OBJ writes an object line immediately before the
                // original mesh's contiguous vertex block. Remember that
                // boundary so an untouched decode/cook path can retain the
                // source vertex numbering rather than assigning vertices in
                // first-face-use order.
                sourceGroupStarts[currentSourceGroup] = vertices.Count;
                continue;
            }
            if (!line.StartsWith("f ", StringComparison.Ordinal))
                continue;
            var indices = line[2..].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (indices.Length != 3)
                throw new InvalidDataException("cook-blender requires triangulated OBJ faces.");
            var a = ParseObjVertexIndex(indices[0]) - 1;
            var b = ParseObjVertexIndex(indices[1]) - 1;
            var c = ParseObjVertexIndex(indices[2]) - 1;
            if (a >= vertices.Count || b >= vertices.Count || c >= vertices.Count)
                throw new InvalidDataException("OBJ face references a vertex that has not been declared.");
            faces.Add(new CookFace(a, b, c, currentMaterial.SurfaceId, currentMaterial.ExtraFlag, currentMaterial.FaceRemap, currentSourceGroup, currentMaterial.ContactCode));
        }
        if ((vertices.Count == 0 || faces.Count == 0) && primitives is not { Count: > 0 })
            throw new InvalidDataException("OBJ contains no vertices or triangulated faces.");
        var groupRanges = new Dictionary<int, VertexRange>();
        foreach (var group in sourceGroupStarts.OrderBy(item => item.Key).ToArray())
        {
            var next = sourceGroupStarts.Where(item => item.Key > group.Key)
                .Select(item => item.Value).DefaultIfEmpty(vertices.Count).Min();
            if (next > group.Value)
                groupRanges[group.Key] = new VertexRange(group.Value, next - group.Value);
        }
        return new CookInput(vertices, faces, groupRanges, primitives);
    }

    private static IReadOnlyList<CookMesh> PartitionCookMesh(CookInput input, bool spatialOrder = false)
    {
        // Per-section caps just above what retail ships (largest retail mesh:
        // 402 vertices / 489 triangles, Field_03L / Field_14O), so every retail
        // mesh stays one section. Index and face-remap widths widen to u16
        // automatically above 255.
        const int maxVertices = 512;
        const int maxTriangles = 512;
        var result = new List<CookMesh>();
        var localVertices = new List<Vec3>();
        var triangles = new List<Triangle>();
        var surfaceIds = new List<ushort>();
        var extraFlags = new List<byte>();
        var faceRemaps = new List<ushort?>();
        var contactCodes = new List<uint?>();
        var remap = new Dictionary<int, int>();
        int? sourceGroup = null;

        void BeginSourceGroup(int group)
        {
            sourceGroup = group;
            if (!input.SourceGroupRanges.TryGetValue(group, out var range) || range.Count > maxVertices)
                return;
            // Only trust a range if every face belonging to the object falls
            // inside it. Blender exports often put all vertices before object
            // declarations; those inputs correctly fall back to the generic
            // first-use remap below.
            if (input.Faces.Where(face => face.SourceGroup == group)
                .Any(face => face.A < range.Start || face.B < range.Start || face.C < range.Start
                    || face.A >= range.Start + range.Count || face.B >= range.Start + range.Count || face.C >= range.Start + range.Count))
                return;
            for (var index = 0; index < range.Count; index++)
            {
                remap[range.Start + index] = index;
                localVertices.Add(input.Vertices[range.Start + index]);
            }
        }

        void FinishMesh()
        {
            if (triangles.Count == 0)
                return;
            var preserveRemap = faceRemaps.All(item => item.HasValue)
                && faceRemaps.Select(item => item!.Value).Distinct().Count() == faceRemaps.Count;
            var remaps = preserveRemap ? faceRemaps.Select(item => item!.Value).ToArray() : Enumerable.Range(0, triangles.Count).Select(item => checked((ushort)item)).ToArray();
            result.Add(new CookMesh(localVertices.ToArray(), triangles.ToArray(), surfaceIds.ToArray(), extraFlags.ToArray(), remaps, contactCodes.ToArray()));
            localVertices = new List<Vec3>();
            triangles = new List<Triangle>();
            surfaceIds = new List<ushort>();
            extraFlags = new List<byte>();
            faceRemaps = new List<ushort?>();
            contactCodes = new List<uint?>();
            remap = new Dictionary<int, int>();
            sourceGroup = null;
        }

        var faces = spatialOrder ? SpatialOrderFaces(input) : input.Faces;
        foreach (var face in faces)
        {
            // Decoded source OBJ files emit one object per NXS/MESH. Face-remap
            // values are a permutation, so a value of zero can occur anywhere;
            // preserve the actual object boundary instead.
            if (sourceGroup.HasValue && face.SourceGroup != sourceGroup.Value)
                FinishMesh();
            if (!sourceGroup.HasValue)
                BeginSourceGroup(face.SourceGroup);
            var globals = new[] { face.A, face.B, face.C };
            var newVertices = globals.Distinct().Count(index => !remap.ContainsKey(index));
            if (triangles.Count == maxTriangles || localVertices.Count + newVertices > maxVertices)
                FinishMesh();
            var local = new int[3];
            for (var i = 0; i < globals.Length; i++)
            {
                if (!remap.TryGetValue(globals[i], out var index))
                {
                    index = localVertices.Count;
                    remap.Add(globals[i], index);
                    localVertices.Add(input.Vertices[globals[i]]);
                }
                local[i] = index;
            }
            triangles.Add(new Triangle(local[0], local[1], local[2]));
            surfaceIds.Add(face.SurfaceId);
            extraFlags.Add(face.ExtraFlag);
            faceRemaps.Add(face.FaceRemap);
            contactCodes.Add(face.ContactCode);
        }
        FinishMesh();
        return result;
    }

    private static IReadOnlyList<CookFace> SpatialOrderFaces(CookInput input)
    {
        // PhysX BVH33 first permutes triangles into spatially local ranges, then
        // stores a face-remap entry alongside each resulting record. A Morton
        // order is a deterministic approximation of that spatial permutation.
        return input.Faces
            .Select((face, index) => new { face, index })
            .GroupBy(item => item.face.SourceGroup)
            .SelectMany(group =>
            {
                var centers = group.Select(item => TriangleCenter(input.Vertices, item.face)).ToArray();
                var bounds = Bounds(centers);
                return group
                    .Select((item, localIndex) => new { item.face, item.index, Key = MortonKey(centers[localIndex], bounds) })
                    .OrderBy(item => item.Key)
                    .ThenBy(item => item.index)
                    .Select(item => item.face);
            })
            .ToArray();
    }

    private static Vec3 TriangleCenter(IReadOnlyList<Vec3> vertices, CookFace face)
    {
        var a = vertices[face.A]; var b = vertices[face.B]; var c = vertices[face.C];
        return new Vec3((a.X + b.X + c.X) / 3f, (a.Y + b.Y + c.Y) / 3f, (a.Z + b.Z + c.Z) / 3f);
    }

    private static uint MortonKey(Vec3 point, Bounds3 bounds)
    {
        static uint Quantize(float value, float min, float max)
        {
            var span = Math.Max(max - min, 0.0001f);
            return (uint)Math.Clamp((int)((value - min) / span * 1023f), 0, 1023);
        }
        static uint Spread(uint value)
        {
            value = (value | (value << 16)) & 0x030000FF;
            value = (value | (value << 8)) & 0x0300F00F;
            value = (value | (value << 4)) & 0x030C30C3;
            value = (value | (value << 2)) & 0x09249249;
            return value;
        }
        return Spread(Quantize(point.X, bounds.Min.X, bounds.Max.X))
            | (Spread(Quantize(point.Y, bounds.Min.Y, bounds.Max.Y)) << 1)
            | (Spread(Quantize(point.Z, bounds.Min.Z, bounds.Max.Z)) << 2);
    }

    private static byte[] BuildCookedCidb(CidbContainer template, CidbGeometry templateGeometry, IReadOnlyList<CookMesh> meshes, int maxLeafTriangles = 16)
    {
        var sections = new List<byte[]>(meshes.Count);
        for (var index = 0; index < meshes.Count; index++)
        {
            var templateMesh = templateGeometry.Meshes[Math.Min(index, templateGeometry.Meshes.Count - 1)];
            var resolved = ResolveContactMaterials(templateMesh, meshes[index]);
            var tailWord = ReadTemplateTailWord(template.CidbData, templateMesh);
            var payload = BuildCookedMeshPayload(resolved.Mesh, templateMesh, tailWord, maxLeafTriangles);
            var materialCodeTable = BuildMaterialCodeTable(resolved.MaterialCodes);
            var section = new byte[0x2C + payload.Length + materialCodeTable.Length];
            Buffer.BlockCopy(template.CidbData, templateMesh.DescriptorOffset, section, 0, 0x2C);
            WriteUInt32(section, 4, (uint)payload.Length);
            WriteUInt32(section, 8, (uint)materialCodeTable.Length);
            Buffer.BlockCopy(payload, 0, section, 0x2C, payload.Length);
            Buffer.BlockCopy(materialCodeTable, 0, section, 0x2C + payload.Length, materialCodeTable.Length);
            sections.Add(section);
        }

        return BuildCidbContainer(template, sections);
    }

    // Compatibility path for authored collision which needs different triangle
    // connectivity but can retain a donor mesh's record counts. It avoids an
    // unnecessary fresh tree where the donor's leaf layout is already valid.
    // The corrected fresh cooker now preserves the descriptor and serialized
    // contact-code table; its vanilla TownWell round trip retained behaviour.
    private static byte[] BuildCidbWithTemplateTree(CidbContainer template, CidbGeometry templateGeometry, IReadOnlyList<CookMesh> meshes)
    {
        if (meshes.Count != templateGeometry.Meshes.Count)
            throw new InvalidDataException($"The custom OBJ produces {meshes.Count} collision mesh(es), but the donor CIDB has {templateGeometry.Meshes.Count}. Keep one OBJ object per donor collision mesh.");

        var cidb = template.CidbData.ToArray();
        for (var index = 0; index < meshes.Count; index++)
        {
            var donor = templateGeometry.Meshes[index];
            var authored = meshes[index];
            if (authored.Vertices.Count != donor.Vertices.Count || authored.Triangles.Count != donor.Triangles.Count)
                throw new InvalidDataException($"Custom collision mesh {index + 1} has {authored.Vertices.Count} vertices and {authored.Triangles.Count} triangles; donor mesh {index + 1} requires exactly {donor.Vertices.Count} vertices and {donor.Triangles.Count} triangles.");
            if (authored.SurfaceIds.Count != donor.Triangles.Count || authored.ExtraFlags.Count != donor.Triangles.Count)
                throw new InvalidDataException($"Custom collision mesh {index + 1} has incomplete per-triangle material metadata.");
            EnsureMaterialIndicesFit(donor, authored.SurfaceIds, $"Custom collision mesh {index + 1}");

            var vertexOffset = donor.Offset + 28;
            for (var vertex = 0; vertex < authored.Vertices.Count; vertex++)
            {
                var value = authored.Vertices[vertex];
                WriteSingle(cidb, vertexOffset + vertex * 12, value.X);
                WriteSingle(cidb, vertexOffset + vertex * 12 + 4, value.Y);
                WriteSingle(cidb, vertexOffset + vertex * 12 + 8, value.Z);
            }

            var triangleOffset = vertexOffset + donor.Vertices.Count * 12;
            for (var triangle = 0; triangle < authored.Triangles.Count; triangle++)
            {
                var value = authored.Triangles[triangle];
                if (value.A is < 0 or > 255 || value.B is < 0 or > 255 || value.C is < 0 or > 255)
                    throw new InvalidDataException($"Custom collision mesh {index + 1} has a triangle index outside the CIDB u8 range.");
                cidb[triangleOffset + triangle * 3] = (byte)value.A;
                cidb[triangleOffset + triangle * 3 + 1] = (byte)value.B;
                cidb[triangleOffset + triangle * 3 + 2] = (byte)value.C;
                WriteUInt16(cidb, donor.SurfaceIdsOffset + triangle * 2, authored.SurfaceIds[triangle]);
            }

            var pageCount = ReadUInt32BigEndian(cidb, donor.RtreeOffset + 0x58);
            var tailOffset = checked(donor.RtreeOffset + 0x60 + (int)pageCount * RtreePageBytes);
            RequireRange(cidb, tailOffset, checked(32 + authored.ExtraFlags.Count), $"NXS/MESH tail for donor mesh {index + 1}");
            if (ReadUInt32(cidb, tailOffset + 28) != authored.Triangles.Count)
                throw new InvalidDataException($"Donor mesh {index + 1} has an incompatible RTRE tail triangle count.");
            for (var triangle = 0; triangle < authored.ExtraFlags.Count; triangle++)
                cidb[tailOffset + 32 + triangle] = authored.ExtraFlags[triangle];

            var refitMesh = donor with
            {
                Vertices = authored.Vertices,
                Triangles = authored.Triangles,
                SurfaceIds = authored.SurfaceIds,
                ExtraFlags = authored.ExtraFlags,
            };
            RefitRtree(cidb, donor.RtreeOffset, refitMesh, authored.Vertices);
        }

        if (template.CidbOffset == 0)
            return cidb;
        var wrapped = template.OriginalData.ToArray();
        Buffer.BlockCopy(cidb, 0, wrapped, template.CidbOffset, cidb.Length);
        return wrapped;
    }

    private static byte[] BuildCookedCidbPhysx(CidbContainer template, CidbGeometry templateGeometry, IReadOnlyList<CookMesh> meshes, int tradeoff = 8, IReadOnlyList<PrimitiveSpec>? primitives = null)
    {
        var sections = new List<byte[]>(meshes.Count);
        for (var index = 0; index < meshes.Count; index++)
        {
            var templateMesh = templateGeometry.Meshes[Math.Min(index, templateGeometry.Meshes.Count - 1)];
            var (prepared, tree, codes) = CookPhysxSection(templateMesh, meshes[index], tradeoff);
            var payload = BuildCookedMeshPayload(prepared, templateMesh, ReadTemplateTailWord(template.CidbData, templateMesh), prebuiltTree: tree);
            var materialCodeTable = BuildMaterialCodeTable(codes);
            var section = new byte[0x2C + payload.Length + materialCodeTable.Length];
            Buffer.BlockCopy(template.CidbData, templateMesh.DescriptorOffset, section, 0, 0x2C);
            WriteUInt32(section, 4, (uint)payload.Length);
            WriteUInt32(section, 8, (uint)materialCodeTable.Length);
            Buffer.BlockCopy(payload, 0, section, 0x2C, payload.Length);
            Buffer.BlockCopy(materialCodeTable, 0, section, 0x2C + payload.Length, materialCodeTable.Length);
            sections.Add(section);
        }
        return BuildCidbContainer(template, sections, primitives);
    }

    // Resolve per-face LAS_Contact codes into this section's contact table
    // first (input order), then run the verified PhysX permutation. Slots and
    // codes travel with their triangles through the cook.
    private static (CookMesh Mesh, byte[] Tree, IReadOnlyList<uint> Codes) CookPhysxSection(CollisionMesh templateMesh, CookMesh mesh, int tradeoff)
    {
        var resolved = ResolveContactMaterials(templateMesh, mesh);
        var prepared = PreparePhysxMesh(NormalizePhysxInput(resolved.Mesh), out var tree, tradeoff);
        // Active-edge flags are derived data: recompute them on the final
        // triangle order (verified identical on all 4,657 retail meshes), so
        // new or edited faces get correct flags instead of 0.
        prepared = prepared with { ExtraFlags = ComputeActiveEdgeFlags(prepared.Vertices, prepared.Triangles) };
        return (prepared, tree, resolved.MaterialCodes);
    }

    private static byte[] BuildCookedCidbPhysxQuick(CidbContainer template, CidbGeometry templateGeometry, IReadOnlyList<CookMesh> meshes, int tradeoff)
    {
        var sections = new List<byte[]>(meshes.Count);
        for (var index = 0; index < meshes.Count; index++)
        {
            var templateMesh = templateGeometry.Meshes[Math.Min(index, templateGeometry.Meshes.Count - 1)];
            var prepared = PreparePhysxQuickMesh(NormalizePhysxInput(meshes[index]), tradeoff, out var tree);
            var payload = BuildCookedMeshPayload(prepared, templateMesh, ReadTemplateTailWord(template.CidbData, templateMesh), prebuiltTree: tree);
            var materialCodeTable = BuildMaterialCodeTable(templateMesh, prepared);
            var section = new byte[0x2C + payload.Length + materialCodeTable.Length];
            Buffer.BlockCopy(template.CidbData, templateMesh.DescriptorOffset, section, 0, 0x2C);
            WriteUInt32(section, 4, (uint)payload.Length);
            WriteUInt32(section, 8, (uint)materialCodeTable.Length);
            Buffer.BlockCopy(payload, 0, section, 0x2C, payload.Length);
            Buffer.BlockCopy(materialCodeTable, 0, section, 0x2C + payload.Length, materialCodeTable.Length);
            sections.Add(section);
        }
        return BuildCidbContainer(template, sections);
    }

    private static byte[] BuildCookedCidbPhysxAtOriginalOffsets(CidbContainer template, CidbGeometry templateGeometry, IReadOnlyList<CookMesh> meshes, IReadOnlyList<PrimitiveSpec>? primitives = null)
    {
        if (meshes.Count != templateGeometry.Meshes.Count)
            throw new InvalidDataException("Layout-preserving PhysX cooking requires the template's collision-mesh count.");
        if (primitives != null)
        {
            var authored = PrimitiveSections(template, primitives);
            var original = ReadNonMeshSections(template.CidbData);
            if (authored.Count != original.Count || authored.Zip(original).Any(pair => !pair.First.AsSpan().SequenceEqual(pair.Second)))
                throw new InvalidDataException("Primitive shapes were edited; the layout-preserving cook cannot move or resize sections. Use cook-blender-physx.");
        }

        var cidb = template.CidbData.ToArray();
        for (var index = 0; index < meshes.Count; index++)
        {
            var templateMesh = templateGeometry.Meshes[index];
            var sectionOffset = templateMesh.Offset - 0x2C;
            var (nextOffset, clearEnd) = SectionSlot(template.CidbData, sectionOffset);
            var (prepared, tree, codes) = CookPhysxSection(templateMesh, meshes[index], 8);
            var payload = BuildCookedMeshPayload(prepared, templateMesh, ReadTemplateTailWord(template.CidbData, templateMesh), prebuiltTree: tree);
            var materialCodeTable = BuildMaterialCodeTable(codes);
            var sectionLength = checked(0x2C + payload.Length + materialCodeTable.Length);
            if (sectionLength > nextOffset - sectionOffset)
                throw new InvalidDataException($"PhysX-cooked mesh {index + 1} needs 0x{sectionLength:X} bytes but its template slot holds 0x{nextOffset - sectionOffset:X}. A resized CIDB cannot retain original offsets.");

            // Do not rewrite the 0x2C-byte descriptor. It contains game-side
            // relocation/layout fields that the standalone CIDB parser does not
            // yet fully name. This makes the candidate a genuine payload-only
            // experiment against a known-good retail collision object.
            WriteUInt32(cidb, sectionOffset + 4, (uint)payload.Length);
            WriteUInt32(cidb, sectionOffset + 8, (uint)materialCodeTable.Length);
            Buffer.BlockCopy(payload, 0, cidb, sectionOffset + 0x2C, payload.Length);
            Buffer.BlockCopy(materialCodeTable, 0, cidb, sectionOffset + 0x2C + payload.Length, materialCodeTable.Length);
            if (clearEnd > sectionOffset + sectionLength)
                Array.Clear(cidb, sectionOffset + sectionLength, clearEnd - (sectionOffset + sectionLength));
        }

        if (template.CidbOffset == 0)
            return cidb;
        var wrapped = template.OriginalData.ToArray();
        Buffer.BlockCopy(cidb, 0, wrapped, template.CidbOffset, cidb.Length);
        return wrapped;
    }

    private static byte[] BuildCookedCidbAtOriginalOffsets(CidbContainer template, CidbGeometry templateGeometry, IReadOnlyList<CookMesh> meshes)
    {
        if (meshes.Count != templateGeometry.Meshes.Count)
            throw new InvalidDataException("Layout-preserving cooking requires the same number of collision meshes as the template.");

        var cidb = template.CidbData.ToArray();
        for (var index = 0; index < meshes.Count; index++)
        {
            var templateMesh = templateGeometry.Meshes[index];
            var sectionOffset = templateMesh.Offset - 0x2C;
            var (nextOffset, clearEnd) = SectionSlot(template.CidbData, sectionOffset);
            var payload = BuildCookedMeshPayload(meshes[index], templateMesh, ReadTemplateTailWord(template.CidbData, templateMesh));
            var materialCodeTable = BuildMaterialCodeTable(templateMesh, meshes[index]);
            var sectionLength = checked(0x2C + payload.Length + materialCodeTable.Length);
            if (sectionLength > nextOffset - sectionOffset)
                throw new InvalidDataException($"Cooked mesh {index + 1} needs 0x{sectionLength:X} bytes but its original CIDB slot holds only 0x{nextOffset - sectionOffset:X}. Use cook-blender for a resized container.");

            // Keep the original descriptor and position. This isolates whether
            // external game-side material lookups depend on the original layout.
            WriteUInt32(cidb, sectionOffset + 4, (uint)payload.Length);
            WriteUInt32(cidb, sectionOffset + 8, (uint)materialCodeTable.Length);
            Buffer.BlockCopy(payload, 0, cidb, sectionOffset + 0x2C, payload.Length);
            Buffer.BlockCopy(materialCodeTable, 0, cidb, sectionOffset + 0x2C + payload.Length, materialCodeTable.Length);
            if (clearEnd > sectionOffset + sectionLength)
                Array.Clear(cidb, sectionOffset + sectionLength, clearEnd - (sectionOffset + sectionLength));
        }

        if (template.CidbOffset == 0)
            return cidb;
        var wrapped = template.OriginalData.ToArray();
        Buffer.BlockCopy(cidb, 0, wrapped, template.CidbOffset, cidb.Length);
        return wrapped;
    }

    private static List<byte[]> ReadRawCidbSections(byte[] cidb)
    {
        if (cidb.Length < 0x10 || !cidb.AsSpan(0, 4).SequenceEqual(CidbMagic))
            throw new InvalidDataException("Not a CIDB container (expected CIDB magic).");
        var count = ReadUInt32(cidb, 0x0C);
        RequireRange(cidb, 0x10, checked((int)count * 4), "CIDB section-offset table");
        var sections = new List<byte[]>((int)count);
        for (var index = 0; index < count; index++)
        {
            var offset = checked((int)ReadUInt32(cidb, 0x10 + index * 4));
            RequireRange(cidb, offset, 0x2C, $"CIDB section {index + 1}");
            var payloadLength = ReadUInt32(cidb, offset + 4);
            var length = checked(0x2C + (int)payloadLength);
            RequireRange(cidb, offset, length, $"CIDB section {index + 1} payload");
            sections.Add(cidb.AsSpan(offset, length).ToArray());
        }
        return sections;
    }

    private static void EnsureMaterialIndicesFit(CollisionMesh templateMesh, IReadOnlyList<ushort> materialIndices, string context)
    {
        var requiredEntries = materialIndices.Count == 0 ? 0 : materialIndices.Max(index => (int)index) + 1;
        if (requiredEntries > templateMesh.MaterialCodes.Count)
            throw new InvalidDataException($"{context} uses material-table index {requiredEntries - 1}, but its donor CIDB mesh has only {templateMesh.MaterialCodes.Count} serialized contact-code entries. Choose an index present in the donor table or use a template with the needed material profile.");
    }

    private static byte[] BuildMaterialCodeTable(CollisionMesh templateMesh, CookMesh mesh)
    {
        EnsureMaterialIndicesFit(templateMesh, mesh.SurfaceIds, $"Cooked mesh {templateMesh.Index}");
        return BuildMaterialCodeTable(templateMesh.MaterialCodes);
    }

    private static byte[] BuildMaterialCodeTable(IReadOnlyList<uint> materialCodes)
    {
        var result = new byte[checked(materialCodes.Count * 4)];
        for (var index = 0; index < materialCodes.Count; index++)
            WriteUInt32(result, index * 4, materialCodes[index]);
        return result;
    }

    // A Blender LAS_Contact_0xXXXXXXXX material carries a complete runtime
    // contact code.  Convert it here, after OBJ partitioning, because CIDB
    // slots are local to the newly created collision mesh rather than global.
    private static ResolvedCookMesh ResolveContactMaterials(CollisionMesh templateMesh, CookMesh mesh)
    {
        if (mesh.ContactCodes is null || mesh.ContactCodes.All(code => !code.HasValue))
        {
            EnsureMaterialIndicesFit(templateMesh, mesh.SurfaceIds, $"Cooked mesh {templateMesh.Index}");
            return new ResolvedCookMesh(mesh, templateMesh.MaterialCodes);
        }

        if (mesh.ContactCodes.Count != mesh.SurfaceIds.Count)
            throw new InvalidDataException("Custom collision mesh has incomplete per-triangle contact-code metadata.");
        var codes = templateMesh.MaterialCodes.ToList();
        var slots = mesh.SurfaceIds.ToArray();
        for (var triangle = 0; triangle < slots.Length; triangle++)
        {
            var requestedCode = mesh.ContactCodes[triangle];
            if (!requestedCode.HasValue)
                continue;
            var slot = codes.IndexOf(requestedCode.Value);
            if (slot < 0)
            {
                slot = codes.Count;
                if (slot > ushort.MaxValue)
                    throw new InvalidDataException("CIDB contact-code table exceeds the u16 material-slot limit.");
                codes.Add(requestedCode.Value);
            }
            slots[triangle] = checked((ushort)slot);
        }
        return new ResolvedCookMesh(mesh with { SurfaceIds = slots, ContactCodes = null }, codes);
    }

    // CIDB container: header word 0x0C is the section count, then u32 section
    // offsets. A section is a 0x2C descriptor + payload (+0x04 bytes) + contact
    // table (+0x08 bytes). Descriptor +0x10 low byte: 0 = primitive shape,
    // 1/2 = NXS triangle mesh. Primitive payloads: box half-extents (3 floats)
    // or sphere radius (1 float). They must be carried through any re-cook.
    private static int[] ReadSectionOffsets(byte[] cidb)
    {
        if (cidb.Length < 0x10 || ReadUInt32(cidb, 4) != 0x10) return Array.Empty<int>();
        var count = (int)ReadUInt32(cidb, 0x0C);
        if (count < 0 || 0x10 + count * 4 > cidb.Length) return Array.Empty<int>();
        return Enumerable.Range(0, count).Select(index => (int)ReadUInt32(cidb, 0x10 + index * 4)).ToArray();
    }

    private static bool IsMeshSection(byte[] cidb, int offset) =>
        offset + 0x2C + NxsMeshMagic.Length <= cidb.Length && cidb.AsSpan(offset + 0x2C, NxsMeshMagic.Length).SequenceEqual(NxsMeshMagic);

    private static int SectionLength(byte[] cidb, int offset) =>
        checked(0x2C + (int)ReadUInt32(cidb, offset + 4) + (int)ReadUInt32(cidb, offset + 8));

    private static IReadOnlyList<byte[]> ReadNonMeshSections(byte[] cidb) =>
        ReadSectionOffsets(cidb)
            .Where(offset => offset + 0x2C <= cidb.Length && !IsMeshSection(cidb, offset))
            .Select(offset => cidb.AsSpan(offset, Math.Min(SectionLength(cidb, offset), cidb.Length - offset)).ToArray())
            .ToArray();

    // Retail section placement: pad the previous end to 4 bytes, then start at
    // the next 0x80 boundary strictly after it (an end already on a 0x80
    // boundary still gets a full 0x80 of padding). Fits every retail container.
    private static int NextSectionStart(int end) => checked((Align(end, 4) / 0x80 + 1) * 0x80);

    // Byte range a layout-preserving rewrite may use for one section: up to the
    // next section of any kind (primitives can follow the last mesh), and the
    // range to zero is only the section's own 0x80 padding, never past the CIDB.
    private static (int Next, int ClearEnd) SectionSlot(byte[] cidb, int sectionOffset)
    {
        var later = ReadSectionOffsets(cidb).Where(offset => offset > sectionOffset).ToArray();
        var next = later.Length > 0 ? later.Min() : cidb.Length;
        var ownEnd = sectionOffset + SectionLength(cidb, sectionOffset);
        // Last section: an embedded slice can continue straight into the next
        // BFRES external file, so only the section's own previous bytes are ours.
        var clearEnd = later.Length > 0 ? Math.Min(next, Align(ownEnd, 0x80)) : Math.Min(next, ownEnd);
        return (next, clearEnd);
    }

    // "#las_primitive box group=1 pos=x,y,z rot=x,y,z size=hx,hy,hz codes=0xC,0x80000"
    // Game coordinates; rotation in radians. "#las_primitives" alone declares
    // an explicit (possibly empty) primitive list that replaces the template's.
    private static PrimitiveSpec ParsePrimitiveLine(string line)
    {
        var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
            throw new InvalidDataException($"Invalid primitive line: {line}");
        var shape = parts[1].ToLowerInvariant() switch
        {
            "box" => 1, "sphere" => 3,
            var other => int.TryParse(other, NumberStyles.Integer, CultureInfo.InvariantCulture, out var raw) ? raw : throw new InvalidDataException($"Unknown primitive shape '{other}'."),
        };
        var group = 0; var position = new Vec3(0, 0, 0); var rotation = new Vec3(0, 0, 0);
        float[] size = Array.Empty<float>(); uint[] codes = Array.Empty<uint>();
        static float[] Floats(string text) => text.Split(',').Select(value => float.Parse(value, NumberStyles.Float, CultureInfo.InvariantCulture)).ToArray();
        foreach (var part in parts.Skip(2))
        {
            var eq = part.IndexOf('=');
            if (eq < 0) throw new InvalidDataException($"Invalid primitive field '{part}'.");
            var key = part[..eq]; var value = part[(eq + 1)..];
            switch (key)
            {
                case "group": group = int.Parse(value, NumberStyles.Integer, CultureInfo.InvariantCulture); break;
                case "pos": { var f = Floats(value); position = new Vec3(f[0], f[1], f[2]); break; }
                case "rot": { var f = Floats(value); rotation = new Vec3(f[0], f[1], f[2]); break; }
                case "size": size = Floats(value); break;
                case "codes": codes = value.Split(',').Select(ParseContactCode).ToArray(); break;
                default: throw new InvalidDataException($"Unknown primitive field '{key}'.");
            }
        }
        if (shape == 1 && size.Length != 3) throw new InvalidDataException("A box primitive needs size=hx,hy,hz (half-extents).");
        if (shape == 3 && size.Length != 1) throw new InvalidDataException("A sphere primitive needs size=radius.");
        if (codes.Length == 0) throw new InvalidDataException("A primitive needs at least one contact code (codes=0x...).");
        return new PrimitiveSpec(shape, group, position, rotation, size, codes);
    }

    private static byte[] BuildPrimitiveSection(PrimitiveSpec spec)
    {
        var payload = spec.Size.Count * 4;
        var section = new byte[0x2C + payload + spec.Codes.Count * 4];
        WriteUInt32(section, 0x04, (uint)payload);
        WriteUInt32(section, 0x08, (uint)(spec.Codes.Count * 4));
        WriteUInt32(section, 0x0C, (uint)((spec.Group << 16) | (spec.Shape & 0xFFFF)));
        WriteUInt32(section, 0x10, 0xFFFFFF00u);
        WriteSingle(section, 0x14, spec.Position.X); WriteSingle(section, 0x18, spec.Position.Y); WriteSingle(section, 0x1C, spec.Position.Z);
        WriteSingle(section, 0x20, spec.Rotation.X); WriteSingle(section, 0x24, spec.Rotation.Y); WriteSingle(section, 0x28, spec.Rotation.Z);
        for (var i = 0; i < spec.Size.Count; i++) WriteSingle(section, 0x2C + i * 4, spec.Size[i]);
        for (var i = 0; i < spec.Codes.Count; i++) WriteUInt32(section, 0x2C + payload + i * 4, spec.Codes[i]);
        return section;
    }

    private static IReadOnlyList<byte[]> PrimitiveSections(CidbContainer template, IReadOnlyList<PrimitiveSpec>? primitives) =>
        primitives == null ? ReadNonMeshSections(template.CidbData) : primitives.Select(BuildPrimitiveSection).ToArray();

    private static byte[] BuildCidbContainer(CidbContainer template, IReadOnlyList<byte[]> sections, IReadOnlyList<PrimitiveSpec>? primitives = null)
    {
        // Callers pass cooked mesh sections only; keep the template's primitive
        // shapes (boxes/spheres) so a re-cook never deletes them.
        sections = sections.Concat(PrimitiveSections(template, primitives)).ToArray();
        var headerEnd = 0x10 + sections.Count * 4;
        var firstSectionOffset = NextSectionStart(headerEnd);
        var offsets = new int[sections.Count];
        var cursor = firstSectionOffset;
        for (var index = 0; index < sections.Count; index++)
        {
            offsets[index] = cursor;
            // Retail: every section starts on 0x80; the container ends directly
            // after the last section (no trailing alignment).
            cursor = index + 1 < sections.Count ? NextSectionStart(cursor + sections[index].Length) : cursor + sections[index].Length;
        }
        var cidb = new byte[cursor];
        Buffer.BlockCopy(CidbMagic, 0, cidb, 0, CidbMagic.Length);
        WriteUInt32(cidb, 4, 0x10);
        WriteUInt32(cidb, 0x0C, (uint)sections.Count);
        for (var index = 0; index < sections.Count; index++)
        {
            WriteUInt32(cidb, 0x10 + index * 4, (uint)offsets[index]);
            Buffer.BlockCopy(sections[index], 0, cidb, offsets[index], sections[index].Length);
        }

        if (template.CidbOffset == 0)
            return cidb;
        var wrapped = new byte[cidb.Length + template.CidbOffset];
        Buffer.BlockCopy(template.OriginalData, 0, wrapped, 0, template.CidbOffset);
        Buffer.BlockCopy(cidb, 0, wrapped, template.CidbOffset, cidb.Length);
        return wrapped;
    }

    private static byte[] BuildCookedMeshPayload(CookMesh mesh, CollisionMesh templateMesh, uint tailWord, int maxLeafTriangles = 16, byte[]? prebuiltTree = null)
    {
        var tree = prebuiltTree ?? BuildRtree(mesh.Vertices, mesh.Triangles, maxLeafTriangles);
        // TriangleMeshBuilder::save: index width follows the largest referenced
        // vertex index; the face-remap width follows the largest remap value.
        var maxVertexIndex = mesh.Triangles.Count == 0 ? 0 : mesh.Triangles.Max(t => Math.Max(t.A, Math.Max(t.B, t.C)));
        if (maxVertexIndex > 0xFFFF)
            throw new InvalidDataException("CIDB meshes support at most 65536 vertices.");
        var indexWidth = maxVertexIndex <= 0xFF ? 1 : 2;
        var maxRemap = mesh.FaceRemaps.Count == 0 ? 0u : mesh.FaceRemaps.Max(value => (uint)value);
        var remapWidth = RemapWidth(maxRemap);
        var length = checked(28 + mesh.Vertices.Count * 12 + mesh.Triangles.Count * 3 * indexWidth + mesh.Triangles.Count * 2
            + 4 + mesh.Triangles.Count * remapWidth + tree.Length + 4 + 24 + 4 + mesh.Triangles.Count);
        var payload = new byte[length];
        Buffer.BlockCopy(NxsMeshMagic, 0, payload, 0, NxsMeshMagic.Length);
        WriteUInt32(payload, 8, templateMesh.HeaderField0);
        WriteUInt32(payload, 12, templateMesh.HeaderField1);
        // PhysX serialization flags: per-triangle material indices and face
        // remaps are present in LAS, followed by the stored index-width flag.
        WriteUInt32(payload, 16, MeshFlagMaterialIndices | MeshFlagFaceRemap | (indexWidth == 1 ? MeshFlag8BitIndices : MeshFlag16BitIndices));
        WriteUInt32(payload, 20, (uint)mesh.Vertices.Count);
        WriteUInt32(payload, 24, (uint)mesh.Triangles.Count);
        var cursor = 28;
        foreach (var vertex in mesh.Vertices)
        {
            WriteSingle(payload, cursor, vertex.X);
            WriteSingle(payload, cursor + 4, vertex.Y);
            WriteSingle(payload, cursor + 8, vertex.Z);
            cursor += 12;
        }
        foreach (var triangle in mesh.Triangles)
        {
            if (indexWidth == 1)
            {
                payload[cursor++] = checked((byte)triangle.A);
                payload[cursor++] = checked((byte)triangle.B);
                payload[cursor++] = checked((byte)triangle.C);
            }
            else
            {
                WriteUInt16(payload, cursor, checked((ushort)triangle.A)); cursor += 2;
                WriteUInt16(payload, cursor, checked((ushort)triangle.B)); cursor += 2;
                WriteUInt16(payload, cursor, checked((ushort)triangle.C)); cursor += 2;
            }
        }
        foreach (var surfaceId in mesh.SurfaceIds)
        {
            WriteUInt16(payload, cursor, surfaceId);
            cursor += 2;
        }
        WriteUInt32(payload, cursor, maxRemap);
        cursor += 4;
        for (var triangle = 0; triangle < mesh.Triangles.Count; triangle++)
        {
            if (remapWidth == 1)
                payload[cursor++] = checked((byte)mesh.FaceRemaps[triangle]);
            else
            {
                WriteUInt16(payload, cursor, mesh.FaceRemaps[triangle]);
                cursor += 2;
            }
        }
        Buffer.BlockCopy(tree, 0, payload, cursor, tree.Length);
        cursor += tree.Length;
        WriteUInt32(payload, cursor, tailWord);
        cursor += 4;
        var rawBounds = Bounds(mesh.Vertices);
        WriteSingle(payload, cursor, rawBounds.Min.X); WriteSingle(payload, cursor + 4, rawBounds.Min.Y); WriteSingle(payload, cursor + 8, rawBounds.Min.Z);
        WriteSingle(payload, cursor + 12, rawBounds.Max.X); WriteSingle(payload, cursor + 16, rawBounds.Max.Y); WriteSingle(payload, cursor + 20, rawBounds.Max.Z);
        cursor += 24;
        WriteUInt32(payload, cursor, (uint)mesh.Triangles.Count);
        cursor += 4;
        // PhysX mExtraTrigData stores active-edge flags. Existing decoded OBJ
        // files expose it in their material name so unchanged geometry retains
        // the exact collision-edge behavior. It is not the water-material field.
        for (var triangle = 0; triangle < mesh.ExtraFlags.Count; triangle++)
            payload[cursor + triangle] = mesh.ExtraFlags[triangle];
        return payload;
    }

    private static uint ReadTemplateTailWord(byte[] cidb, CollisionMesh mesh)
    {
        var pages = ReadUInt32BigEndian(cidb, mesh.RtreeOffset + 0x58);
        var tailOffset = checked(mesh.RtreeOffset + 0x60 + (int)pages * RtreePageBytes);
        RequireRange(cidb, tailOffset, 4, "CIDB mesh tail");
        return ReadUInt32(cidb, tailOffset);
    }

    private static int GetMeshIndexWidth(uint serialFlags, uint vertexCount)
    {
        var has8BitIndices = (serialFlags & MeshFlag8BitIndices) != 0;
        var has16BitIndices = (serialFlags & MeshFlag16BitIndices) != 0;
        if (has8BitIndices == has16BitIndices)
            throw new InvalidDataException($"NXS/MESH has unsupported index-width flags 0x{serialFlags:X}.");
        if (has8BitIndices && vertexCount > byte.MaxValue)
            throw new InvalidDataException($"NXS/MESH flags select u8 indices but has {vertexCount} vertices.");
        return has8BitIndices ? 1 : 2;
    }

    private static byte[] BuildRtree(IReadOnlyList<Vec3> vertices, IReadOnlyList<Triangle> triangles, int maxLeafTriangles = 16)
    {
        if (maxLeafTriangles is < 1 or > 16)
            throw new ArgumentOutOfRangeException(nameof(maxLeafTriangles), "RTRE leaf size must be from 1 through 16 triangles.");
        var leaves = new List<BuildNode>();
        for (var firstTriangle = 0; firstTriangle < triangles.Count; firstTriangle += maxLeafTriangles)
        {
            var count = Math.Min(maxLeafTriangles, triangles.Count - firstTriangle);
            var bounds = BoundsOfTriangles(triangles, vertices, firstTriangle, count);
            var pointer = ((uint)firstTriangle << 5) | ((uint)(count - 1) << 1) | 1;
            leaves.Add(new BuildNode(bounds, pointer));
        }

        var pages = new List<BuildPage>();
        var root = BuildRtreePage(leaves, pages);
        var result = new byte[0x60 + pages.Count * RtreePageBytes];
        Buffer.BlockCopy(RtreeMagic, 0, result, 0, RtreeMagic.Length);
        WriteUInt32BigEndian(result, 4, 1);
        WriteRtreeHeaderBounds(result, 0, root.Bounds);
        WriteUInt32BigEndian(result, 0x48, 4);
        WriteUInt32BigEndian(result, 0x4C, 1);
        WriteUInt32BigEndian(result, 0x50, (uint)root.Level);
        WriteUInt32BigEndian(result, 0x54, (uint)(pages.Count * 4));
        WriteUInt32BigEndian(result, 0x58, (uint)pages.Count);
        WriteUInt32BigEndian(result, 0x5C, 0);
        for (var pageIndex = 0; pageIndex < pages.Count; pageIndex++)
        {
            var pageOffset = 0x60 + pageIndex * RtreePageBytes;
            for (var slot = 0; slot < 4; slot++)
            {
                var node = pages[pageIndex].Nodes[slot];
                if (node == null)
                {
                    WriteNodeBounds(result, pageOffset, slot, new Bounds3(
                        new Vec3(float.MaxValue, float.MaxValue, float.MaxValue),
                        new Vec3(-float.MaxValue, -float.MaxValue, -float.MaxValue)));
                    WriteUInt32BigEndian(result, pageOffset + 0x60 + slot * 4, 0);
                }
                else
                {
                    WriteNodeBounds(result, pageOffset, slot, node.Bounds);
                    WriteUInt32BigEndian(result, pageOffset + 0x60 + slot * 4, node.Pointer);
                }
            }
        }
        return result;
    }

    // Port of the BVH33 SAH partitioning strategy used by PhysX 3.4's
    // RTreeCooking. Unlike the early prototype, this permutes the stored
    // triangle records into the same spatial ranges referenced by RTRE leaves.
    private static CookMesh PreparePhysxMesh(CookMesh mesh, out byte[] tree, int tradeoff = 8)
    {
        int[] terminalTriangleCounts = { 64, 60, 56, 48, 46, 44, 40, 36, 32, 28, 24, 20, 16, 12, 12 };
        int[] leafTriangleCounts = { 16, 14, 12, 10, 9, 8, 8, 6, 5, 5, 5, 4, 4, 4, 2 };
        var permutation = Enumerable.Range(0, mesh.Triangles.Count).ToList();
        var bounds = mesh.Triangles.Select(triangle => BoundsOfTriangles(new[] { triangle }, mesh.Vertices, 0, 1)).ToArray();
        var context = new PhysxSahContext(bounds);
        var nodes = new List<PhysxNode>();
        var maxLevels = 0;
        BuildPhysxSahPage(permutation, 0, permutation.Count, context, nodes, ref maxLevels, 0, terminalTriangleCounts[tradeoff], leafTriangleCounts[tradeoff]);
        tree = WritePhysxRtree(nodes, BoundsOfBounds(bounds), maxLevels);
        return new CookMesh(
            mesh.Vertices,
            permutation.Select(index => mesh.Triangles[index]).ToArray(),
            permutation.Select(index => mesh.SurfaceIds[index]).ToArray(),
            permutation.Select(index => mesh.ExtraFlags[index]).ToArray(),
            permutation.Select(index => mesh.FaceRemaps[index]).ToArray());
    }

    private static CookMesh NormalizePhysxInput(CookMesh mesh)
    {
        // A decoded CIDB contains post-cook triangles. mFaceRemap says which
        // pre-cook input face each stored triangle came from. Reconstruct that
        // input order before applying the PhysX permutation again; otherwise a
        // second cook starts from the wrong face identity sequence.
        var count = mesh.Triangles.Count;
        // Face ids can have gaps (faces removed by mesh cleaning); the cooker
        // received the surviving faces in ascending original-id order. The
        // original ids are kept so the re-cooked remap is directly comparable.
        if (mesh.FaceRemaps.Count != count || mesh.FaceRemaps.Distinct().Count() != count)
            return mesh;
        var sourceRows = Enumerable.Range(0, count).OrderBy(stored => mesh.FaceRemaps[stored]).ToArray();
        return new CookMesh(
            mesh.Vertices,
            sourceRows.Select(index => mesh.Triangles[index]).ToArray(),
            sourceRows.Select(index => mesh.SurfaceIds[index]).ToArray(),
            sourceRows.Select(index => mesh.ExtraFlags[index]).ToArray(),
            sourceRows.Select(index => mesh.FaceRemaps[index]).ToArray());
    }

    // Literal port of PhysX 3.4 SubSortQuick::sort4 and QuickSelect.h. The
    // stored triangle sequence is the resulting permutation, just as with the
    // SAH path. This lets decoded vanilla rooms tell us which cook path was
    // used before we attempt newly-authored geometry.
    private static CookMesh PreparePhysxQuickMesh(CookMesh mesh, int tradeoff, out byte[] tree)
    {
        int[] maxBoundsPerLeafPage = { 16, 14, 12, 10, 8, 7, 6, 5, 4 };
        var permutation = Enumerable.Range(0, mesh.Triangles.Count).ToList();
        var bounds = mesh.Triangles.Select(triangle => BoundsOfTriangles(new[] { triangle }, mesh.Vertices, 0, 1)).ToArray();
        var nodes = new List<PhysxNode>();
        var levels = 0;
        BuildPhysxQuickPage(permutation, 0, permutation.Count, bounds, maxBoundsPerLeafPage[tradeoff], nodes, ref levels, 0);
        tree = WritePhysxRtree(nodes, BoundsOfBounds(bounds), levels);
        return new CookMesh(
            mesh.Vertices,
            permutation.Select(index => mesh.Triangles[index]).ToArray(),
            permutation.Select(index => mesh.SurfaceIds[index]).ToArray(),
            permutation.Select(index => mesh.ExtraFlags[index]).ToArray(),
            permutation.Select(index => mesh.FaceRemaps[index]).ToArray());
    }

    private static void BuildPhysxQuickPage(List<int> order, int start, int count, IReadOnlyList<Bounds3> bounds, int maxBoundsPerLeafPage, List<PhysxNode> nodes, ref int maxLevels, int level)
    {
        maxLevels = level == 0 ? 1 : Math.Max(maxLevels, level + 1);
        var cluster4 = Math.Max(count / 4, 1);
        var clusterBounds = BoundsOfBounds(order.Skip(start).Take(count).Select(index => bounds[index]));
        var maxElements = new[]
        {
            clusterBounds.Max.X - clusterBounds.Min.X,
            clusterBounds.Max.Y - clusterBounds.Min.Y,
            clusterBounds.Max.Z - clusterBounds.Min.Z,
        };
        var axis = LongestPhysxAxis(maxElements);
        var pageStart = nodes.Count;
        for (var slot = 0; slot < 4; slot++) nodes.Add(new PhysxNode());
        var leftover = Math.Max(count - cluster4 * 3, 0);

        for (var slot = 0; slot < 4; slot++)
        {
            var offset = cluster4 * slot;
            var childCount = slot < 3 ? cluster4 : leftover;
            if (slot < 3 && offset <= count - 1)
            {
                QuickSelectFirstK(order, start + offset, start + count - 1, cluster4,
                    (left, right) => Center(bounds[left], axis) <= Center(bounds[right], axis));
                maxElements[axis] *= slot switch { 0 => 0.8f, 1 => 0.7f, _ => 0.6f };
                axis = LongestPhysxAxis(maxElements);
            }

            var node = nodes[pageStart + slot];
            if (childCount == 0 || offset + childCount > count)
                continue;
            // A non-empty node begins as an internal node. PhysxNode uses -1
            // for the serialized empty-node sentinel, so leaving that default
            // here would suppress recursion and collapse the whole tree to one
            // page.
            node.LeafCount = 0;
            node.Bounds = BoundsOfBounds(order.Skip(start + offset).Take(childCount).Select(index => bounds[index]));
            if (childCount <= maxBoundsPerLeafPage)
            {
                node.LeafStart = start + offset;
                node.LeafCount = childCount;
            }
        }

        for (var slot = 0; slot < 4; slot++)
        {
            var offset = cluster4 * slot;
            var childCount = slot < 3 ? cluster4 : leftover;
            var node = nodes[pageStart + slot];
            if (childCount != 0 && node.LeafCount == 0)
            {
                node.ChildStart = nodes.Count;
                BuildPhysxQuickPage(order, start + offset, childCount, bounds, maxBoundsPerLeafPage, nodes, ref maxLevels, level + 1);
            }
        }
    }

    private static int LongestPhysxAxis(IReadOnlyList<float> values) =>
        values[0] > values[1] && values[0] > values[2] ? 0 : values[1] > values[2] ? 1 : 2;

    private static void QuickSelectFirstK(List<int> values, int left, int right, int k, Func<int, int, bool> lessOrEqual)
    {
        while (true)
        {
            var pivotIndex = (left + right) >> 1;
            var pivotValue = values[pivotIndex];
            (values[pivotIndex], values[right]) = (values[right], values[pivotIndex]);
            var store = left;
            for (var index = left; index < right; index++)
            {
                if (!lessOrEqual(values[index], pivotValue)) continue;
                (values[index], values[store]) = (values[store], values[index]);
                store++;
            }
            (values[store], values[right]) = (values[right], values[store]);
            var pivotDistance = store - left + 1;
            if (pivotDistance == k) return;
            if (k < pivotDistance)
                right = store - 1;
            else
            {
                k -= pivotDistance;
                left = store + 1;
            }
        }
    }

    private static void BuildPhysxSahPage(List<int> order, int start, int count, PhysxSahContext context, List<PhysxNode> nodes, ref int maxLevels, int level, int terminalTriangleCount, int leafTriangleCount)
    {
        maxLevels = Math.Max(maxLevels, level + 1);
        var splits = SplitPhysxCluster(order, start, count, context);
        var pageStart = nodes.Count;
        for (var slot = 0; slot < 4; slot++)
            nodes.Add(new PhysxNode());
        // PhysX evaluates and may clear this flag while iterating the slots;
        // a slot sees the value as it stands at that point (sort4, okSAH).
        var terminalByPage = count <= terminalTriangleCount && splits.All(split => split.Count <= 16);

        for (var slot = 0; slot < 4; slot++)
        {
            var split = splits[slot];
            var node = nodes[pageStart + slot];
            if (split.Count == 0)
                continue;
            node.LeafCount = 0;
            node.Bounds = BoundsOfBounds(split.Indices(order).Select(index => context.Bounds[index]));
            var areas = split.Indices(order).Select(index => Sah(context.Bounds[index])).ToArray();
            var minArea = areas.Min();
            var maxArea = areas.Max();
            var similarArea = minArea > 0f && maxArea / minArea < 40f;
            if (!similarArea)
                terminalByPage = false;
            var leaf = split.Count <= 2 || (similarArea && split.Count <= 3) || terminalByPage || split.Count <= leafTriangleCount;
            if (leaf)
            {
                node.LeafStart = split.Start;
                node.LeafCount = split.Count;
            }
        }

        if (terminalByPage)
            return;
        for (var slot = 0; slot < 4; slot++)
        {
            var node = nodes[pageStart + slot];
            var split = splits[slot];
            if (split.Count != 0 && node.LeafCount == 0)
            {
                node.ChildStart = nodes.Count;
                BuildPhysxSahPage(order, split.Start, split.Count, context, nodes, ref maxLevels, level + 1, terminalTriangleCount, leafTriangleCount);
            }
        }
    }

    private static PhysxSplit[] SplitPhysxCluster(List<int> order, int start, int count, PhysxSahContext context)
    {
        if (count == 0)
            return new[] { new PhysxSplit(start, 0), new PhysxSplit(start, 0), new PhysxSplit(start, 0), new PhysxSplit(start, 0) };
        if (count < 4)
        {
            // sort4 with clusterSize < RTREE_N calls no split() at all: splitPos
            // starts as {1,2,3} and is clamped to clusterSize.
            var splitPos = new[] { 1, 2, 3, count };
            for (var index = count; index < 3; index++) splitPos[index] = count;
            Array.Sort(splitPos, 0, 3);
            var small = new PhysxSplit[4];
            small[0] = new PhysxSplit(start, splitPos[0]);
            for (var index = 1; index < 4; index++)
                small[index] = new PhysxSplit(start + splitPos[index - 1], splitPos[index] - splitPos[index - 1]);
            return small;
        }
        var intervals = new List<PhysxSplit> { new(start, count) };
        while (intervals.Count < 4)
        {
            var target = intervals.Select((interval, index) => new { interval, index, Score = interval.Count > 1 ? SahRange(order, interval, context.Bounds) * interval.Count : -1f })
                .OrderByDescending(item => item.Score).ThenBy(item => item.index).First();
            if (target.interval.Count <= 1)
            {
                intervals.Add(new PhysxSplit(start + count, 0));
                continue;
            }
            var split = FindPhysxSplit(order, target.interval, context);
            // InlineArray::replaceWithLast is observable when equal-area
            // intervals are considered later: it leaves the right half in the
            // replaced slot and appends the left half. Preserve that seemingly
            // odd order instead of using List.RemoveAt, which changes tie
            // selection and therefore the serialized triangle permutation.
            intervals.Add(new PhysxSplit(target.interval.Start, split));
            intervals.Add(new PhysxSplit(target.interval.Start + split, target.interval.Count - split));
            intervals[target.index] = intervals[^1];
            intervals.RemoveAt(intervals.Count - 1);
        }
        return intervals.OrderBy(interval => interval.Start).ToArray();
    }

    private static int FindPhysxSplit(List<int> order, PhysxSplit interval, PhysxSahContext context)
    {
        // SubSortSAH::split returns before sorting for tiny clusters, so the
        // current triangle order must be left untouched in these cases.
        if (interval.Count <= 1)
            return 0;
        if (interval.Count == 2)
            return 1;
        var minCount = interval.Count >= 4 ? 2 : 1;
        var axisScores = new float[3];
        var axisSplits = new int[3];
        for (var axis = 0; axis < 3; axis++)
        {
            var sorted = context.SortedSubset(order, interval, axis);
            // This intentionally mirrors PhysX 3.4 SubSortSAH::split(),
            // including its inclusive sweep limits. A conventional left/right
            // SAH implementation produces different split ties and pages.
            var splitStartLeft = minCount;
            var splitEndLeft = interval.Count - minCount;
            var splitStartRight = interval.Count - splitStartLeft;
            var splitEndRight = interval.Count - splitEndLeft;
            var localScore = float.MaxValue;
            var localSplit = 0;
            var metricsLeft = new List<float>();
            var leftBounds = context.Bounds[sorted[0]];
            for (var index = 1; index < splitStartLeft; index++)
                leftBounds = BoundsOfBounds(new[] { leftBounds, context.Bounds[sorted[index]] });
            for (var index = splitStartLeft; index <= splitEndLeft; index++)
            {
                leftBounds = BoundsOfBounds(new[] { leftBounds, context.Bounds[sorted[index]] });
                metricsLeft.Add(Sah(leftBounds));
            }
            var metricsRight = new List<float>();
            var rightBounds = context.Bounds[sorted[^1]];
            for (var index = interval.Count - 2; index > splitStartRight; index--)
                rightBounds = BoundsOfBounds(new[] { rightBounds, context.Bounds[sorted[index]] });
            for (var index = splitStartRight; index >= splitEndRight; index--)
            {
                rightBounds = BoundsOfBounds(new[] { rightBounds, context.Bounds[sorted[index]] });
                metricsRight.Add(Sah(rightBounds));
            }
            for (var index = 0; index < metricsLeft.Count; index++)
            {
                var offset = index + splitStartLeft;
                var countLeft = (float)(index + minCount);
                var countRight = (float)(metricsLeft.Count - index - 1 + minCount);
                var score = countLeft * metricsLeft[index] + countRight * metricsRight[metricsLeft.Count - index - 1];
                var currentDistance = Math.Abs(offset - interval.Count / 2);
                var bestDistance = Math.Abs(localSplit - interval.Count / 2);
                if (score < localScore || (score <= localScore && currentDistance < bestDistance))
                {
                    localScore = score;
                    localSplit = offset;
                }
            }
            axisScores[axis] = localScore;
            axisSplits[axis] = localSplit;
            if (TraceSplits)
                Console.WriteLine($"    split n={interval.Count} axis={axis} sorted=[{string.Join(",", sorted)}] L=[{string.Join(",", metricsLeft.Select(v => v.ToString("R")))}] R=[{string.Join(",", metricsRight.Select(v => v.ToString("R")))}] best={localScore:R}@{localSplit}");
        }
        // PhysX resolves an equal axis score in X/Y/Z order. The tie-break for
        // a more-even split is only within one axis, not across axes.
        var bestAxis = axisScores[0] <= axisScores[1] && axisScores[0] <= axisScores[2]
            ? 0 : axisScores[1] <= axisScores[2] ? 1 : 2;
        var bestSplit = axisSplits[bestAxis];
        var reordered = context.SortedSubset(order, interval, bestAxis);
        for (var index = 0; index < reordered.Length; index++)
            order[interval.Start + index] = reordered[index];
        return interval.Count == 3 && bestSplit == 0 ? 1 : bestSplit;
    }

    private static int[] SortPhysxIndices(IEnumerable<int> indices, IReadOnlyList<Bounds3> bounds, int axis)
    {
        var sorted = indices.ToArray();
        PhysxSort(sorted, (left, right) => Center(bounds[left], axis) < Center(bounds[right], axis));
        return sorted;
    }

    // Literal control flow port of PxShared Ps::sort / PsSortInternals. Using
    // Array.Sort changes ordering of equal-centre grid triangles and therefore
    // changes a serialized BVH33 tree even when all geometry is identical.
    private static void PhysxSort<T>(T[] elements, Func<T, T, bool> less)
    {
        if (elements.Length < 2)
            return;
        void Swap(int left, int right) => (elements[left], elements[right]) = (elements[right], elements[left]);
        void Median3(int first, int last)
        {
            var middle = (first + last) / 2;
            if (less(elements[middle], elements[first])) Swap(first, middle);
            if (less(elements[last], elements[first])) Swap(first, last);
            if (less(elements[last], elements[middle])) Swap(middle, last);
            Swap(middle, last - 1);
        }
        int Partition(int first, int last)
        {
            Median3(first, last);
            var left = first;
            var right = last - 1;
            while (true)
            {
                while (less(elements[++left], elements[last - 1])) { }
                while (less(elements[last - 1], elements[--right])) { }
                if (left >= right) break;
                Swap(left, right);
            }
            Swap(left, last - 1);
            return left;
        }
        void SmallSort(int first, int last)
        {
            for (var left = first; left < last; left++)
            {
                var minimum = left;
                for (var right = left + 1; right <= last; right++)
                    if (less(elements[right], elements[minimum])) minimum = right;
                if (minimum != left) Swap(minimum, left);
            }
        }
        var pending = new Stack<(int First, int Last)>();
        var first = 0;
        var last = elements.Length - 1;
        while (true)
        {
            while (last > first)
            {
                if (last - first < 5)
                {
                    SmallSort(first, last);
                    break;
                }
                var part = Partition(first, last);
                if (part - first < last - part)
                {
                    pending.Push((first, part - 1));
                    first = part + 1;
                }
                else
                {
                    pending.Push((part + 1, last));
                    last = part - 1;
                }
            }
            if (pending.Count == 0) break;
            var next = pending.Pop(); first = next.First; last = next.Last;
        }
    }

    private static int RemapWidth(uint maxIndex) => maxIndex <= 0xFF ? 1 : maxIndex <= 0xFFFF ? 2 : 4;

    private static float Center(Bounds3 bounds, int axis) => axis switch { 0 => bounds.Min.X + bounds.Max.X, 1 => bounds.Min.Y + bounds.Max.Y, _ => bounds.Min.Z + bounds.Max.Z };
    // PxSAH = V3Dot(v, V3PermZXY(v)). Retail CIDBs were cooked on PC, so the
    // x86 SSE V3Dot evaluation order applies: (ay*by + aw*bw) + (ax*bx + az*bz)
    // with b = PermZXY(a) gives y*x + (x*z + z*y). Float addition is not
    // associative; the NEON order or a naive x*y + y*z + x*z flips SAH ties and
    // reorders triangles (verified against every retail mesh with verify-cooker).
    private static float Sah(Bounds3 bounds)
    {
        var x = bounds.Max.X - bounds.Min.X; var y = bounds.Max.Y - bounds.Min.Y; var z = bounds.Max.Z - bounds.Min.Z;
        return y * x + (x * z + z * y);
    }
    private static float SahRange(IReadOnlyList<int> order, PhysxSplit split, IReadOnlyList<Bounds3> bounds) => SahOfIndices(split.Indices(order), bounds);
    private static float SahOfIndices(IEnumerable<int> indices, IReadOnlyList<Bounds3> bounds) => Sah(BoundsOfBounds(indices.Select(index => bounds[index])));

    private static byte[] WritePhysxRtree(IReadOnlyList<PhysxNode> nodes, Bounds3 bounds, int levels)
    {
        var pages = (nodes.Count + 3) / 4;
        var result = new byte[0x60 + pages * RtreePageBytes];
        Buffer.BlockCopy(RtreeMagic, 0, result, 0, RtreeMagic.Length);
        WriteUInt32BigEndian(result, 4, 1); WriteRtreeHeaderBounds(result, 0, bounds);
        WriteUInt32BigEndian(result, 0x48, 4); WriteUInt32BigEndian(result, 0x4C, 1); WriteUInt32BigEndian(result, 0x50, (uint)levels);
        WriteUInt32BigEndian(result, 0x54, (uint)(pages * 4)); WriteUInt32BigEndian(result, 0x58, (uint)pages);
        var firstEmptyIndex = -1;
        for (var index = 0; index < pages * 4; index++)
        {
            var pageOffset = 0x60 + index / 4 * RtreePageBytes; var slot = index % 4;
            if (index >= nodes.Count || nodes[index].LeafCount < 0)
            {
                // RTreeCooking: every empty node points at the first empty node
                // (index * sizeof(RTreeNodeQ) = 28) and is flagged as a leaf.
                if (firstEmptyIndex < 0) firstEmptyIndex = index;
                WriteNodeBounds(result, pageOffset, slot, new Bounds3(new Vec3(float.MaxValue, float.MaxValue, float.MaxValue), new Vec3(-float.MaxValue, -float.MaxValue, -float.MaxValue)));
                WriteUInt32BigEndian(result, pageOffset + 0x60 + slot * 4, (uint)(firstEmptyIndex * 28) | 1u);
                continue;
            }
            var node = nodes[index]; WriteNodeBounds(result, pageOffset, slot, node.Bounds);
            var pointer = node.LeafCount > 0 ? ((uint)node.LeafStart << 5) | ((uint)(node.LeafCount - 1) << 1) | 1u : (uint)(node.ChildStart * 28);
            WriteUInt32BigEndian(result, pageOffset + 0x60 + slot * 4, pointer);
        }
        return result;
    }

    private static BuildPageResult BuildRtreePage(IReadOnlyList<BuildNode> nodes, List<BuildPage> pages)
    {
        var pageIndex = pages.Count;
        var page = new BuildPage();
        pages.Add(page);
        if (nodes.Count <= 4)
        {
            for (var index = 0; index < nodes.Count; index++)
                page.Nodes[index] = nodes[index];
            return new BuildPageResult(pageIndex, BoundsOfNodes(nodes), 1);
        }

        var childCount = Math.Min(4, nodes.Count);
        var perChild = (int)Math.Ceiling(nodes.Count / (double)childCount);
        var maxLevel = 0;
        var childBounds = new List<Bounds3>();
        for (var child = 0; child < childCount; child++)
        {
            var start = child * perChild;
            var count = Math.Min(perChild, nodes.Count - start);
            if (count <= 0)
                continue;
            var childResult = BuildRtreePage(nodes.Skip(start).Take(count).ToArray(), pages);
            page.Nodes[child] = new BuildNode(childResult.Bounds, (uint)(childResult.PageIndex * RtreePageBytes));
            childBounds.Add(childResult.Bounds);
            maxLevel = Math.Max(maxLevel, childResult.Level);
        }
        return new BuildPageResult(pageIndex, BoundsOfBounds(childBounds), maxLevel + 1);
    }

    private static Bounds3 BoundsOfNodes(IReadOnlyList<BuildNode> nodes) => BoundsOfBounds(nodes.Select(node => node.Bounds));

    private static Bounds3 BoundsOfBounds(IEnumerable<Bounds3> bounds)
    {
        var list = bounds.ToArray();
        if (list.Length == 0)
            throw new InvalidDataException("Cannot build an RTRE page with no collision leaves.");
        var min = list[0].Min;
        var max = list[0].Max;
        foreach (var item in list.Skip(1))
        {
            Include(ref min, ref max, item.Min);
            Include(ref min, ref max, item.Max);
        }
        return new Bounds3(min, max);
    }

    private static int Align(int value, int alignment) => checked((value + alignment - 1) / alignment * alignment);

    private static Dictionary<int, IReadOnlyList<ushort>> ValidateSurfaceOnlyObj(CidbGeometry geometry, ObjDocument obj)
    {
        var expectedVertexCount = geometry.Meshes.Sum(mesh => mesh.Vertices.Count);
        if (obj.Vertices.Count != expectedVertexCount)
            throw new InvalidDataException($"OBJ has {obj.Vertices.Count} vertices; expected {expectedVertexCount}. Geometry edits are not supported yet.");
        var objectsByName = obj.Objects.ToDictionary(item => item.Name, StringComparer.Ordinal);
        var result = new Dictionary<int, IReadOnlyList<ushort>>();
        var vertexBase = 1;
        foreach (var mesh in geometry.Meshes)
        {
            var name = $"collision_mesh_{mesh.Index:D3}_at_{mesh.Offset:X}";
            if (!objectsByName.TryGetValue(name, out var objMesh))
                throw new InvalidDataException($"OBJ is missing required object '{name}'. Export the generated collision OBJ without renaming or merging its mesh objects.");
            if (objMesh.Faces.Count != mesh.Triangles.Count)
                throw new InvalidDataException($"OBJ object '{name}' has {objMesh.Faces.Count} faces; expected {mesh.Triangles.Count}. Topology edits are not supported yet.");

            for (var vertex = 0; vertex < mesh.Vertices.Count; vertex++)
            {
                var imported = obj.Vertices[vertexBase - 1 + vertex];
                var raw = obj.IsBlenderCompanion ? FromBlenderDaeSpace(imported) : imported;
                if (!NearlyEqual(raw, mesh.Vertices[vertex]))
                    throw new InvalidDataException($"OBJ vertex {vertexBase + vertex} changed. Geometry editing requires RTRE rebuilding, which is not implemented yet.");
            }

            var editedIds = new List<ushort>(mesh.Triangles.Count);
            for (var triangleIndex = 0; triangleIndex < mesh.Triangles.Count; triangleIndex++)
            {
                var expected = mesh.Triangles[triangleIndex];
                var imported = objMesh.Faces[triangleIndex];
                if (imported.A != vertexBase + expected.A || imported.B != vertexBase + expected.B || imported.C != vertexBase + expected.C)
                    throw new InvalidDataException($"OBJ face {triangleIndex + 1} in '{name}' changed its vertex order or topology. Preserve topology for rebuild-surface.");
                editedIds.Add(imported.SurfaceId);
            }
            result.Add(mesh.Index, editedIds);
            vertexBase += mesh.Vertices.Count;
        }
        return result;
    }

    private static Dictionary<int, MeshEdits> ValidateGeometryObj(CidbGeometry geometry, ObjDocument obj)
    {
        var expectedVertexCount = geometry.Meshes.Sum(mesh => mesh.Vertices.Count);
        if (obj.Vertices.Count != expectedVertexCount)
            throw new InvalidDataException($"OBJ has {obj.Vertices.Count} vertices; expected {expectedVertexCount}. Adding or removing vertices is not supported by rebuild-geometry.");

        var objectsByName = obj.Objects.ToDictionary(item => item.Name, StringComparer.Ordinal);
        var result = new Dictionary<int, MeshEdits>();
        var vertexBase = 1;
        foreach (var mesh in geometry.Meshes)
        {
            var name = $"collision_mesh_{mesh.Index:D3}_at_{mesh.Offset:X}";
            if (!objectsByName.TryGetValue(name, out var objMesh))
                throw new InvalidDataException($"OBJ is missing required object '{name}'. Export the generated collision OBJ without renaming or merging its mesh objects.");
            if (objMesh.Faces.Count != mesh.Triangles.Count)
                throw new InvalidDataException($"OBJ object '{name}' has {objMesh.Faces.Count} faces; expected {mesh.Triangles.Count}. Adding or removing faces is not supported by rebuild-geometry.");

            var vertices = new List<Vec3>(mesh.Vertices.Count);
            for (var vertex = 0; vertex < mesh.Vertices.Count; vertex++)
            {
                var imported = obj.Vertices[vertexBase - 1 + vertex];
                vertices.Add(obj.IsBlenderCompanion ? FromBlenderDaeSpace(imported) : imported);
            }

            var surfaceIds = new List<ushort>(mesh.Triangles.Count);
            for (var triangleIndex = 0; triangleIndex < mesh.Triangles.Count; triangleIndex++)
            {
                var expected = mesh.Triangles[triangleIndex];
                var imported = objMesh.Faces[triangleIndex];
                if (imported.A != vertexBase + expected.A || imported.B != vertexBase + expected.B || imported.C != vertexBase + expected.C)
                    throw new InvalidDataException($"OBJ face {triangleIndex + 1} in '{name}' changed its vertex order or topology. Preserve face order for rebuild-geometry.");
                surfaceIds.Add(imported.SurfaceId);
            }
            result.Add(mesh.Index, new MeshEdits(vertices, surfaceIds));
            vertexBase += mesh.Vertices.Count;
        }
        return result;
    }

    // CIDB's RTRE payload is a big-endian PhysX 3.4 RTree. The per-node pointer
    // already gives us the complete leaf triangle range, so a geometry-only edit
    // can retain its topology and precisely refit every leaf and ancestor page.
    private static void RefitRtree(byte[] output, int rtreeOffset, CollisionMesh mesh, IReadOnlyList<Vec3> vertices)
    {
        RequireRange(output, rtreeOffset, 0x60, $"RTRE header at 0x{rtreeOffset:X}");
        if (!output.AsSpan(rtreeOffset, 4).SequenceEqual(RtreeMagic))
            throw new InvalidDataException($"Expected RTRE marker at 0x{rtreeOffset:X}.");

        var version = ReadUInt32BigEndian(output, rtreeOffset + 4);
        var pageSize = ReadUInt32BigEndian(output, rtreeOffset + 0x48);
        var rootPages = ReadUInt32BigEndian(output, rtreeOffset + 0x4C);
        var totalNodes = ReadUInt32BigEndian(output, rtreeOffset + 0x54);
        var totalPages = ReadUInt32BigEndian(output, rtreeOffset + 0x58);
        if (version is not (1 or 2) || pageSize != 4 || rootPages == 0 || totalPages == 0 || totalNodes != totalPages * pageSize)
            throw new InvalidDataException($"RTRE at 0x{rtreeOffset:X} has an unsupported PhysX layout (version={version}, pageSize={pageSize}, roots={rootPages}, nodes={totalNodes}, pages={totalPages}).");
        if (rootPages > totalPages)
            throw new InvalidDataException($"RTRE at 0x{rtreeOffset:X} declares more root pages than total pages.");
        var pageDataOffset = checked(rtreeOffset + 0x60);
        RequireRange(output, pageDataOffset, checked((int)totalPages * RtreePageBytes), $"RTRE pages at 0x{rtreeOffset:X}");

        // PhysX's own refit walks pages from the end to the beginning. Child pages
        // are written later in the packed array, so this makes their new bounds
        // available when an ancestor is processed.
        for (var pageIndex = (int)totalPages - 1; pageIndex >= 0; pageIndex--)
        {
            var pageOffset = pageDataOffset + pageIndex * RtreePageBytes;
            for (var slot = 0; slot < 4; slot++)
            {
                var nodeOffset = pageOffset + slot * 4;
                var minX = ReadSingleBigEndian(output, pageOffset + slot * 4);
                if (minX == float.MaxValue)
                    continue; // PhysX empty/sentinel slot.

                var pointer = ReadUInt32BigEndian(output, pageOffset + 0x60 + slot * 4);
                Bounds3 bounds;
                if ((pointer & 1) != 0)
                {
                    var count = (int)(((pointer >> 1) & 15) + 1);
                    var firstTriangle = (int)(pointer >> 5);
                    if (firstTriangle < 0 || firstTriangle + count > mesh.Triangles.Count)
                        throw new InvalidDataException($"RTRE leaf at 0x{nodeOffset:X} references triangles {firstTriangle}..{firstTriangle + count - 1}, outside mesh {mesh.Index}.");
                    // Runtime PhysX refits the existing tree from the exact triangle
                    // positions.  The 5e-4 padding belongs to *cooking* only; adding it
                    // here changes every bound even when the mesh is untouched.
                    bounds = BoundsOfTriangles(mesh.Triangles, vertices, firstTriangle, count, inflate: false);
                }
                else
                {
                    if (pointer % RtreePageBytes != 0)
                        throw new InvalidDataException($"RTRE non-leaf pointer 0x{pointer:X} at 0x{nodeOffset:X} is not page-aligned.");
                    var childPage = (int)(pointer / RtreePageBytes);
                    if (childPage <= pageIndex || childPage >= totalPages)
                        throw new InvalidDataException($"RTRE non-leaf at 0x{nodeOffset:X} points to invalid child page {childPage}.");
                    bounds = BoundsOfPage(output, pageDataOffset + childPage * RtreePageBytes);
                }
                WriteNodeBounds(output, pageOffset, slot, bounds);
            }
        }

        // Gu::RTreeTriangleMesh::refitBVH updates TriangleMesh::mAABB from
        // refitAllStaticTree's returned bounds, but leaves the RTree's cooking
        // header (mBoundsMin/mBoundsMax/mDiagonalScaler) alone.  The local AABB
        // is serialized immediately after the RTRE pages.
        var treeBounds = BoundsOfRootPages(output, pageDataOffset, (int)rootPages);
        var meshTailOffset = checked(pageDataOffset + (int)totalPages * RtreePageBytes);
        RequireRange(output, meshTailOffset, 28, $"NXS/MESH local bounds at 0x{meshTailOffset:X}");
        WriteSingle(output, meshTailOffset + 4, treeBounds.Min.X);
        WriteSingle(output, meshTailOffset + 8, treeBounds.Min.Y);
        WriteSingle(output, meshTailOffset + 12, treeBounds.Min.Z);
        WriteSingle(output, meshTailOffset + 16, treeBounds.Max.X);
        WriteSingle(output, meshTailOffset + 20, treeBounds.Max.Y);
        WriteSingle(output, meshTailOffset + 24, treeBounds.Max.Z);
    }

    private const int RtreePageBytes = 0x70;
    private const float RtreeBoundsInflation = 5e-4f;

    private static Bounds3 BoundsOfTriangles(IReadOnlyList<Triangle> triangles, IReadOnlyList<Vec3> vertices, int firstTriangle, int count, bool inflate = true)
    {
        var first = triangles[firstTriangle];
        var min = vertices[first.A];
        var max = min;
        for (var triangleIndex = firstTriangle; triangleIndex < firstTriangle + count; triangleIndex++)
        {
            var triangle = triangles[triangleIndex];
            Include(ref min, ref max, vertices[triangle.A]);
            Include(ref min, ref max, vertices[triangle.B]);
            Include(ref min, ref max, vertices[triangle.C]);
        }
        if (!inflate)
            return new Bounds3(min, max);

        // PhysX RTreeCooking inflates each triangle AABB by 5e-4. This is
        // intentionally not used by the runtime RTree refit path above.
        return new Bounds3(
            new Vec3(min.X - RtreeBoundsInflation, min.Y - RtreeBoundsInflation, min.Z - RtreeBoundsInflation),
            new Vec3(max.X + RtreeBoundsInflation, max.Y + RtreeBoundsInflation, max.Z + RtreeBoundsInflation));
    }

    private static Bounds3 BoundsOfPage(byte[] output, int pageOffset)
    {
        var hasBounds = false;
        var min = new Vec3(0, 0, 0);
        var max = new Vec3(0, 0, 0);
        for (var slot = 0; slot < 4; slot++)
        {
            var slotMinX = ReadSingleBigEndian(output, pageOffset + slot * 4);
            if (slotMinX == float.MaxValue)
                continue;
            var slotMin = new Vec3(slotMinX, ReadSingleBigEndian(output, pageOffset + 0x10 + slot * 4), ReadSingleBigEndian(output, pageOffset + 0x20 + slot * 4));
            var slotMax = new Vec3(ReadSingleBigEndian(output, pageOffset + 0x30 + slot * 4), ReadSingleBigEndian(output, pageOffset + 0x40 + slot * 4), ReadSingleBigEndian(output, pageOffset + 0x50 + slot * 4));
            if (!hasBounds)
            {
                min = slotMin;
                max = slotMax;
                hasBounds = true;
            }
            else
            {
                Include(ref min, ref max, slotMin);
                Include(ref min, ref max, slotMax);
            }
        }
        if (!hasBounds)
            throw new InvalidDataException($"RTRE page at 0x{pageOffset:X} has no non-empty slots.");
        return new Bounds3(min, max);
    }

    private static Bounds3 BoundsOfRootPages(byte[] output, int pageDataOffset, int rootPages)
    {
        var result = BoundsOfPage(output, pageDataOffset);
        var min = result.Min;
        var max = result.Max;
        for (var page = 1; page < rootPages; page++)
        {
            var bounds = BoundsOfPage(output, pageDataOffset + page * RtreePageBytes);
            Include(ref min, ref max, bounds.Min);
            Include(ref min, ref max, bounds.Max);
        }
        return new Bounds3(min, max);
    }

    private static void Include(ref Vec3 min, ref Vec3 max, Vec3 point)
    {
        min = new Vec3(Math.Min(min.X, point.X), Math.Min(min.Y, point.Y), Math.Min(min.Z, point.Z));
        max = new Vec3(Math.Max(max.X, point.X), Math.Max(max.Y, point.Y), Math.Max(max.Z, point.Z));
    }

    private static void WriteNodeBounds(byte[] output, int pageOffset, int slot, Bounds3 bounds)
    {
        WriteSingleBigEndian(output, pageOffset + slot * 4, bounds.Min.X);
        WriteSingleBigEndian(output, pageOffset + 0x10 + slot * 4, bounds.Min.Y);
        WriteSingleBigEndian(output, pageOffset + 0x20 + slot * 4, bounds.Min.Z);
        WriteSingleBigEndian(output, pageOffset + 0x30 + slot * 4, bounds.Max.X);
        WriteSingleBigEndian(output, pageOffset + 0x40 + slot * 4, bounds.Max.Y);
        WriteSingleBigEndian(output, pageOffset + 0x50 + slot * 4, bounds.Max.Z);
    }

    private static void WriteRtreeHeaderBounds(byte[] output, int rtreeOffset, Bounds3 bounds)
    {
        // This matches PhysX RTreeCooking.cpp: mInvDiagonal is all ones and
        // mDiagonalScaler is (max - min) / 65535, with a zero fourth lane.
        var scale = new Vec3((bounds.Max.X - bounds.Min.X) / 65535f, (bounds.Max.Y - bounds.Min.Y) / 65535f, (bounds.Max.Z - bounds.Min.Z) / 65535f);
        WriteVec4BigEndian(output, rtreeOffset + 0x08, bounds.Min, 0f);
        WriteVec4BigEndian(output, rtreeOffset + 0x18, bounds.Max, 0f);
        WriteVec4BigEndian(output, rtreeOffset + 0x28, new Vec3(1f, 1f, 1f), 1f);
        WriteVec4BigEndian(output, rtreeOffset + 0x38, scale, 0f);
    }

    private static void WriteVec4BigEndian(byte[] output, int offset, Vec3 value, float fourth)
    {
        WriteSingleBigEndian(output, offset, value.X);
        WriteSingleBigEndian(output, offset + 4, value.Y);
        WriteSingleBigEndian(output, offset + 8, value.Z);
        WriteSingleBigEndian(output, offset + 12, fourth);
    }

    private static int ParseObjVertexIndex(string token)
    {
        var value = token.Split('/')[0];
        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index) || index <= 0)
            throw new InvalidDataException($"Unsupported OBJ vertex index '{token}'. Use positive absolute indices.");
        return index;
    }

    private static uint ParseContactCode(string value)
    {
        var text = value.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? value[2..] : value;
        if (!uint.TryParse(text, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var code))
            throw new InvalidDataException($"'{value}' is not a valid hexadecimal CIDB contact code.");
        return code;
    }

    private static MaterialKey ParseMaterialKey(string material)
    {
        const string prefix = "cidb_surface_";
        const string contactPrefix = "cidb_contact_";
        if (material.StartsWith(contactPrefix, StringComparison.Ordinal))
        {
            var contactRemainder = material[contactPrefix.Length..];
            var contactExtraSeparator = contactRemainder.IndexOf("_extra_", StringComparison.Ordinal);
            var contactText = contactExtraSeparator >= 0 ? contactRemainder[..contactExtraSeparator] : contactRemainder;
            var contactCode = ParseContactCode(contactText);
            var contactExtraFlag = (byte)0;
            if (contactExtraSeparator >= 0 && !byte.TryParse(contactRemainder[(contactExtraSeparator + 7)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out contactExtraFlag))
                throw new InvalidDataException("CIDB contact material extra flag is invalid.");
            return new MaterialKey(0, contactExtraFlag, null, contactCode);
        }
        if (!material.StartsWith(prefix, StringComparison.Ordinal))
            throw new InvalidDataException("Each OBJ face must use cidb_surface_<slot> or cidb_contact_<full-hex-code>.");
        var remainder = material[prefix.Length..];
        var extraSeparator = remainder.IndexOf("_extra_", StringComparison.Ordinal);
        var remapSeparator = remainder.IndexOf("_remap_", StringComparison.Ordinal);
        var firstSeparator = new[] { extraSeparator, remapSeparator }.Where(index => index >= 0).DefaultIfEmpty(remainder.Length).Min();
        var surfaceText = remainder[..firstSeparator];
        if (!ushort.TryParse(surfaceText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var surfaceId))
            throw new InvalidDataException("CIDB material surface ID is invalid.");
        var extraFlag = (byte)0;
        if (extraSeparator >= 0)
        {
            var extraEnd = remapSeparator > extraSeparator ? remapSeparator : remainder.Length;
            if (!byte.TryParse(remainder[(extraSeparator + 7)..extraEnd], NumberStyles.Integer, CultureInfo.InvariantCulture, out extraFlag))
                throw new InvalidDataException("CIDB material extra flag is invalid.");
        }
        ushort? faceRemap = null;
        if (remapSeparator >= 0)
        {
            if (!ushort.TryParse(remainder[(remapSeparator + 7)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedRemap))
                throw new InvalidDataException("CIDB material face remap is invalid.");
            faceRemap = parsedRemap;
        }
        return new MaterialKey(surfaceId, extraFlag, faceRemap);
    }

    private static Vec3 FromBlenderDaeSpace(Vec3 vertex) => new(vertex.X, -vertex.Z, vertex.Y);
    private static bool NearlyEqual(Vec3 left, Vec3 right) => Math.Abs(left.X - right.X) <= 0.0001f && Math.Abs(left.Y - right.Y) <= 0.0001f && Math.Abs(left.Z - right.Z) <= 0.0001f;

    // Standard right-handed rotation around X by -90 degrees: (x, y, z) -> (x, z, -y).
    private static Vec3 ToBlenderDaeSpace(Vec3 vertex) => new(vertex.X, vertex.Z, -vertex.Y);

    private static void WriteProjectionSvg(string outputPath, CidbGeometry geometry, Projection projection)
    {
        var allVertices = geometry.Meshes.SelectMany(mesh => mesh.Vertices).ToArray();
        if (allVertices.Length == 0)
            return;

        var horizontalMin = allVertices.Min(vertex => vertex.X);
        var horizontalMax = allVertices.Max(vertex => vertex.X);
        // The verified Blender conversion is (x, y, z) -> (x, z, -y). CIDB
        // uses X/Z as its floor plane and Y as its vertical axis.
        var verticalMin = projection == Projection.Top
            ? allVertices.Min(vertex => vertex.Z)
            : allVertices.Min(vertex => vertex.Y);
        var verticalMax = projection == Projection.Top
            ? allVertices.Max(vertex => vertex.Z)
            : allVertices.Max(vertex => vertex.Y);
        var horizontalRange = Math.Max(horizontalMax - horizontalMin, 0.01f);
        var verticalRange = Math.Max(verticalMax - verticalMin, 0.01f);
        var scale = 900f / Math.Max(horizontalRange, verticalRange);
        var usedWidth = horizontalRange * scale;
        var usedHeight = verticalRange * scale;
        var left = 50f + (900f - usedWidth) / 2f;
        var bottom = 950f - (900f - usedHeight) / 2f;
        var axisLabel = projection == Projection.Top ? "Top projection: X / Z (height Y omitted)" : "Elevation projection: X / Y";

        using var writer = new StreamWriter(outputPath, false, new UTF8Encoding(false));
        writer.WriteLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
        writer.WriteLine("<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"1000\" height=\"1000\" viewBox=\"0 0 1000 1000\">");
        writer.WriteLine("  <rect width=\"1000\" height=\"1000\" fill=\"#101217\"/>");
        writer.WriteLine("  <text x=\"20\" y=\"30\" fill=\"#eeeeee\" font-family=\"sans-serif\" font-size=\"18\">LAS CIDB collision — " + axisLabel + "</text>");
        writer.WriteLine("  <g stroke=\"#dfe8ff\" stroke-width=\"0.8\">");
        foreach (var mesh in geometry.Meshes)
        {
            var color = SvgColors[(mesh.Index - 1) % SvgColors.Length];
            writer.WriteLine($"    <g fill=\"{color}\" fill-opacity=\"0.38\" data-mesh=\"{mesh.Index}\">");
            foreach (var triangle in mesh.Triangles)
            {
                var a = Project(mesh.Vertices[triangle.A], projection, horizontalMin, verticalMin, scale, left, bottom);
                var b = Project(mesh.Vertices[triangle.B], projection, horizontalMin, verticalMin, scale, left, bottom);
                var c = Project(mesh.Vertices[triangle.C], projection, horizontalMin, verticalMin, scale, left, bottom);
                writer.WriteLine($"      <polygon points=\"{FormatFloat(a.X)},{FormatFloat(a.Y)} {FormatFloat(b.X)},{FormatFloat(b.Y)} {FormatFloat(c.X)},{FormatFloat(c.Y)}\"/>");
            }
            writer.WriteLine("    </g>");
        }
        writer.WriteLine("  </g>");
        writer.WriteLine($"  <text x=\"20\" y=\"985\" fill=\"#bbbbbb\" font-family=\"sans-serif\" font-size=\"14\">{geometry.Name}: {geometry.Meshes.Sum(mesh => mesh.Triangles.Count)} triangles; each color is one embedded NXS/MESH collision mesh.</text>");
        writer.WriteLine("</svg>");
    }

    private static Vec2 Project(Vec3 vertex, Projection projection, float horizontalMin, float verticalMin, float scale, float left, float bottom)
    {
        var vertical = projection == Projection.Top ? vertex.Z : vertex.Y;
        return new Vec2(left + (vertex.X - horizontalMin) * scale, bottom - (vertical - verticalMin) * scale);
    }

    private static Bounds3 Bounds(IReadOnlyList<Vec3> vertices)
    {
        if (vertices.Count == 0)
            return new Bounds3(new Vec3(0, 0, 0), new Vec3(0, 0, 0));
        // Gu::computeBoundsAroundVertices: start from the LAST vertex, then fold
        // vertices 0..n-2 with SSE _mm_min_ps/_mm_max_ps, which return the second
        // operand (the new vertex) when values compare equal. This decides the
        // sign of zero in the serialized AABB (0.0f vs -0.0f).
        var min = vertices[^1];
        var max = vertices[^1];
        for (var index = 0; index < vertices.Count - 1; index++)
        {
            var vertex = vertices[index];
            min = new Vec3(min.X < vertex.X ? min.X : vertex.X, min.Y < vertex.Y ? min.Y : vertex.Y, min.Z < vertex.Z ? min.Z : vertex.Z);
            max = new Vec3(max.X > vertex.X ? max.X : vertex.X, max.Y > vertex.Y ? max.Y : vertex.Y, max.Z > vertex.Z ? max.Z : vertex.Z);
        }
        return new Bounds3(min, max);
    }

    private static void RequireRange(byte[] data, int offset, int length, string label)
    {
        if (offset < 0 || length < 0 || (long)offset + length > data.Length)
            throw new InvalidDataException($"{label} exceeds the CIDB file boundary.");
    }

    private static float ReadSingle(byte[] data, int offset) => BitConverter.Int32BitsToSingle(BitConverter.ToInt32(data, offset));
    private static uint ReadUInt32BigEndian(byte[] data, int offset) => BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset, 4));
    private static float ReadSingleBigEndian(byte[] data, int offset) => BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(offset, 4)));
    private static void WriteSingle(byte[] data, int offset, float value) => BitConverter.TryWriteBytes(data.AsSpan(offset, 4), value);
    private static void WriteSingleBigEndian(byte[] data, int offset, float value) => BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(offset, 4), BitConverter.SingleToInt32Bits(value));

    private static string FormatFloat(float value) => value.ToString("R", CultureInfo.InvariantCulture);

    private static void PrintReport(CidbReport report)
    {
        Console.WriteLine($"BFRES: {report.InputPath}");
        Console.WriteLine($"File size: 0x{report.FileSize:X} ({report.FileSize:N0} bytes); header size match: {report.DeclaredSizeMatchesFile}");
        Console.WriteLine($"Relocation table: 0x{report.RelocationOffset:X}");
        if (report.Blocks.Count == 0)
        {
            Console.WriteLine("CIDB: none found before the relocation table.");
            return;
        }

        foreach (var block in report.Blocks)
        {
            Console.WriteLine();
            Console.WriteLine($"CIDB at 0x{block.StartOffset:X}; container span ends at 0x{block.EndOffset:X}; {block.Length:N0} bytes");
            Console.WriteLine($"  headerSize=0x{block.HeaderSize:X}; field@0x08=0x{block.VersionOrFlags:X}; sectionCount={block.SectionCount}");
            Console.WriteLine($"  first section offsets: {(block.SectionOffsets.Count == 0 ? "unavailable" : string.Join(", ", block.SectionOffsets.Select(value => $"0x{value:X}")))}");
            Console.WriteLine($"  SHA-256: {block.Sha256}");
        }

        Console.WriteLine();
        Console.WriteLine("Note: the tool preserves the CIDB container span; it does not claim to decode collision triangles yet.");
    }

    private static IEnumerable<int> FindAll(byte[] data, byte[] pattern)
    {
        for (var offset = 0; offset <= data.Length - pattern.Length; offset++)
        {
            if (data.AsSpan(offset, pattern.Length).SequenceEqual(pattern))
                yield return offset;
        }
    }

    private static int FindFirst(byte[] data, byte[] pattern, int startOffset, int endOffset)
    {
        for (var offset = startOffset; offset <= endOffset - pattern.Length; offset++)
        {
            if (data.AsSpan(offset, pattern.Length).SequenceEqual(pattern))
                return offset;
        }
        return -1;
    }

    private static uint ReadUInt32(byte[] data, int offset) => BitConverter.ToUInt32(data, offset);
    private static ushort ReadUInt16(byte[] data, int offset) => BitConverter.ToUInt16(data, offset);
    private static void WriteUInt32(byte[] data, int offset, uint value) => BitConverter.TryWriteBytes(data.AsSpan(offset, 4), value);
    private static void WriteUInt32BigEndian(byte[] data, int offset, uint value) => BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(offset, 4), value);
    private static void WriteUInt16(byte[] data, int offset, ushort value) => BitConverter.TryWriteBytes(data.AsSpan(offset, 2), value);

    private static int Fail(string message)
    {
        Console.Error.WriteLine(message);
        Console.Error.WriteLine("Run CidbTool help for usage.");
        return 2;
    }

    private static void PrintHelp()
    {
        Console.WriteLine($"CidbTool {ToolVersion} - CIDB collision tool for Link's Awakening (Switch) BFRES files");
        Console.WriteLine();
        Console.WriteLine("  CidbTool inspect <room.bfres>");
        Console.WriteLine("  CidbTool extract <room.bfres> [output-directory]");
        Console.WriteLine("  CidbTool decode <room.bfres|extracted.cidb> [output-directory]");
        Console.WriteLine("  CidbTool rebuild-surface <source.cidb> <edited.collision[.blender].obj> <output.cidb>");
        Console.WriteLine("  CidbTool rebuild-geometry <source.cidb> <edited.collision[.blender].obj> <output.cidb>");
        Console.WriteLine("  CidbTool translate <source.cidb> <x> <y> <z> <output.cidb>");
        Console.WriteLine("  CidbTool cook-blender <template.cidb> <custom-blender.obj> <output.cidb>");
        Console.WriteLine("  CidbTool cook-blender-physx <template.cidb> <custom-blender.obj> <output.cidb> [sah-preset-0-14]");
        Console.WriteLine("  CidbTool cook-blender-physx-layout <template.cidb> <custom-blender.obj> <output.cidb>");
        Console.WriteLine("  CidbTool cook-blender-template-tree <template.cidb> <custom-blender.obj> <output.cidb>");
        Console.WriteLine("  CidbTool create-topology-smoke-test <source.cidb> <output.cidb>");
        Console.WriteLine("  CidbTool create-barrier-smoke-test <source.cidb> <output.cidb>");
        Console.WriteLine("  CidbTool restore-mesh <original.cidb> <cooked.cidb> <one-based-mesh-index> <output.cidb>");
        Console.WriteLine("  CidbTool batch <map-directory> [output-directory]");
        Console.WriteLine("  CidbTool surface-catalog <map-directory> [output.json]");
        Console.WriteLine("  CidbTool contact-catalog <map-directory> [output.json]");
        Console.WriteLine("  CidbTool audit <map-directory|room.bfres> [output.json]");
        Console.WriteLine("  CidbTool export-surface-layers <room.bfres|extracted.cidb> [output-directory]");
        Console.WriteLine("  CidbTool create-floor-surface-tests <source.cidb> <output-directory>");
        Console.WriteLine("  CidbTool wrap-toolbox <raw-or-toolbox.cidb> <output.cidb>");
        Console.WriteLine("  CidbTool verify-cooker <map-directory|room.bfres> [report.json] [--only sah8,quick4] [--limit N]");
        Console.WriteLine("  CidbTool verify-recook <map-directory|room.bfres> [--layout]");
        Console.WriteLine("  CidbTool verify-edges <map-directory|room.bfres> [--show N]");
        Console.WriteLine("  CidbTool export-presets <output.json> <map-directory> [more directories...]");
        Console.WriteLine("  CidbTool --version");
        Console.WriteLine();
        Console.WriteLine("extract and batch produce a raw .cidb container slice plus a JSON manifest.");
        Console.WriteLine("decode exports validated NXS/MESH vertices and triangles to a standard OBJ.");
        Console.WriteLine("rebuild-surface accepts only material/surface-ID edits and preserves geometry + RTRE exactly.");
        Console.WriteLine("rebuild-geometry accepts same-topology vertex and surface-ID edits and refits the embedded PhysX RTRE tree.");
        Console.WriteLine("translate creates a diagnostic same-topology collision offset in raw CIDB coordinates and refits RTRE.");
        Console.WriteLine("cook-blender creates new collision topology from a triangulated Blender OBJ and a compatible CIDB template.");
        Console.WriteLine("cook-blender-physx cooks with the verified PhysX 3.4 RTree cooker (SAH preset 8 = the retail settings); -layout keeps the template's section offsets.");
        Console.WriteLine("verify-cooker re-cooks every retail mesh from its face remap and reports byte-exact matches of triangle order, RTRE pages and RTRE header per cooker preset.");
        Console.WriteLine("verify-recook rebuilds whole retail CIDB containers through the cook-blender-physx path and reports byte-identical containers.");
        Console.WriteLine("verify-edges recomputes the active-edge flags (PhysX EdgeList port) and compares them with every retail mesh.");
        Console.WriteLine("cook-blender-physx honours cidb_contact_<hex> faces (LAS_Contact materials) and recomputes edge flags for all faces.");
        Console.WriteLine("create-topology-smoke-test splits one horizontal triangle into three coplanar triangles to validate the arbitrary-topology loading path.");
        Console.WriteLine("create-barrier-smoke-test adds a marked invisible double-sided wall in TownWell to validate new physical collision.");
        Console.WriteLine("cook-blender-template-tree permits new triangle connectivity while preserving each donor mesh's vertex/triangle count and retail RTRE page layout.");
        Console.WriteLine("restore-mesh is a research diagnostic that restores one original mesh inside a cooked CIDB byte-for-byte.");
        Console.WriteLine("surface-catalog inventories the raw u16 surface IDs used by map CIDBs and lists reference rooms; it does not guess their gameplay meanings.");
        Console.WriteLine("contact-catalog inventories each CIDB mesh's serialized u32 contact-code table and its local slots, preserving high-bit behavior flags.");
        Console.WriteLine("audit compares CIDB headers, section descriptors, RTRE headers, and per-triangle raw metadata across a map directory.");
        Console.WriteLine("export-surface-layers writes one Blender-coordinate preview OBJ per raw surface ID; previews are not bake input.");
        Console.WriteLine("create-floor-surface-tests creates IDs 1-10 on the largest existing upward-facing surface-0 floor patch, preserving every other CIDB byte.");
        Console.WriteLine("wrap-toolbox emits the four-byte ExternalFile wrapper Switch Toolbox requires for importing CIDB data.");
        Console.WriteLine("Both rebuild commands preserve all unknown CIDB bytes and never modify the source BFRES or ROMFS file.");
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly string[] SvgColors = { "#ff6b6b", "#4dabf7", "#51cf66", "#fcc419", "#cc5de8", "#20c997", "#ff922b", "#748ffc" };
    private static readonly string[] MtlColors = { "0.85 0.22 0.22", "0.15 0.52 0.88", "0.20 0.72 0.33", "0.95 0.68 0.08", "0.68 0.28 0.80", "0.10 0.72 0.62", "0.95 0.42 0.12", "0.35 0.45 0.88" };

    private sealed record CidbBlock(
        int StartOffset,
        int EndOffset,
        int Length,
        uint HeaderSize,
        uint VersionOrFlags,
        uint SectionCount,
        IReadOnlyList<uint> SectionOffsets,
        string Sha256);

    private sealed record CidbReport(
        string InputPath,
        int FileSize,
        uint DeclaredSize,
        bool DeclaredSizeMatchesFile,
        uint RelocationOffset,
        IReadOnlyList<CidbBlock> Blocks);

    private sealed record ExtractionManifest(CidbReport Source, CidbBlock Cidb, string ExtractedPath)
    {
        public string Boundary = "CIDB magic through BFRES relocation-table boundary";
        public string Note = "This is a lossless extracted container span. Use the decode command to inspect NXS/MESH collision geometry.";
    }

    private sealed record CidbInput(string Name, byte[] Data);
    private sealed record SurfaceCatalogSample(string File, int Mesh, int TriangleCount);
    private sealed record SurfaceCatalogSurface(ushort SurfaceId, int TriangleCount, int RoomCount, int MeshCount, IReadOnlyList<SurfaceCatalogSample> LargestUses);
    private sealed record SurfaceCatalogReport(string MapDirectory, int BfresFilesScanned, int FilesWithCidb, int CidbBlocksDecoded, int MeshesDecoded, IReadOnlyList<SurfaceCatalogSurface> Surfaces, IReadOnlyList<string> Failures)
    {
        public string Meaning = "SurfaceId is the raw per-triangle u16 value from the CIDB NXS/MESH block. This catalog proves which values occur, but only an in-game controlled test can establish semantics such as solid, water, ladder, damaging, or footstep behavior.";
        public string ReferenceSelection = "LargestUses lists the strongest room/mesh examples for each ID. Use original, unmodified ROMFS rooms as the baseline for screenshots and behavior tests.";
    }
    private sealed record ContactCodeSample(string File, int Mesh, int Slot, int TriangleCount);
    private sealed record ContactCodeProfile(uint Code, int LowSixBitCode, int MeshCount, int RoomCount, int TriangleCount, IReadOnlyList<ContactCodeSample> LargestUses);
    private sealed record ContactCatalogReport(string MapDirectory, int BfresFilesScanned, int CidbBlocksDecoded, int MeshesDecoded, IReadOnlyList<ContactCodeProfile> Codes, IReadOnlyList<string> Failures)
    {
        public string Meaning = "Each Code is a full u32 from a CIDB section's serialized contact-code table. Each sample identifies a mesh-local material-table Slot and the number of its triangles. TriangleSurfaceIds select these slots. LowSixBitCode is Code & 0x3F, the field observed in PlayerLink + 0x1700 for footstep logic; retain the full Code because high bits carry additional state.";
        public string Scope = "This catalog is derived only from CIDB bytes and makes no semantic name guesses. It enables automated selection of donor meshes/tables that carry a known vanilla contact profile.";
    }
    private sealed record ValueCount<T>(T Value, int Count);
    private sealed record CidbAuditMesh(
        int MeshIndex,
        int SectionIndex,
        int VertexCount,
        int TriangleCount,
        uint NxsHeaderWord08,
        uint NxsHeaderWord0C,
        uint NxsHeaderWord10,
        uint PreludeFirstWord,
        int PreludeLength,
        bool HasExplicitFaceRemap,
        IReadOnlyList<ValueCount<ushort>> SurfaceIds,
        IReadOnlyList<ValueCount<byte>> ExtraFlags);
    private sealed record CidbAuditContainer(
        string File,
        string Name,
        int CidbSize,
        uint HeaderWord08,
        int SectionCount,
        IReadOnlyList<CidbAuditMesh> Meshes);
    private sealed record CidbAuditReport(
        string MapDirectory,
        int BfresFilesScanned,
        IReadOnlyList<CidbAuditContainer> Containers,
        IReadOnlyList<ValueCount<uint>> ContainerHeaderWord08,
        IReadOnlyList<ValueCount<string>> NxsHeaderWords08To10,
        IReadOnlyList<IReadOnlyList<ValueCount<uint>>> SectionDescriptorWords,
        IReadOnlyList<ValueCount<byte>> ExtraFlagValues,
        IReadOnlyList<ValueCount<string>> SurfaceIdExtraFlagPairs,
        IReadOnlyList<IReadOnlyList<ValueCount<uint>>> RtreeHeaderWords,
        IReadOnlyList<string> Failures)
    {
        public string Scope = "Structural evidence only. Surface IDs and extra-flag bytes are raw values, not inferred gameplay labels.";
        public string SectionDescriptorWordsMeaning = "Each inner list is one little-endian u32 descriptor word at section offsets +0x00 through +0x28. The payload-length word at +0x04 naturally varies with mesh size.";
        public string RtreeHeaderWordsMeaning = "Each inner list is one big-endian u32 word at RTRE +0x04 through +0x18. These describe the PhysX R-tree, not a proven material table.";
    }
    private sealed class SurfaceCatalogAccumulator
    {
        private readonly List<SurfaceCatalogSample> _uses = new();
        private readonly HashSet<string> _rooms = new(StringComparer.OrdinalIgnoreCase);
        public ushort SurfaceId { get; }
        public int TriangleCount { get; private set; }
        public int MeshCount => _uses.Count;

        public SurfaceCatalogAccumulator(ushort surfaceId) => SurfaceId = surfaceId;

        public void Add(string file, int mesh, int triangleCount)
        {
            TriangleCount += triangleCount;
            _rooms.Add(file);
            _uses.Add(new SurfaceCatalogSample(file, mesh, triangleCount));
        }

        public SurfaceCatalogSurface ToReport() => new(
            SurfaceId,
            TriangleCount,
            _rooms.Count,
            MeshCount,
            _uses.OrderByDescending(item => item.TriangleCount)
                .ThenBy(item => item.File, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.Mesh)
                .Take(24)
                .ToArray());
    }
    private sealed class ContactCodeAccumulator
    {
        private readonly List<ContactCodeSample> _uses = new();
        private readonly HashSet<string> _rooms = new(StringComparer.OrdinalIgnoreCase);
        public uint Code { get; }
        public int TriangleCount { get; private set; }
        public ContactCodeAccumulator(uint code) => Code = code;
        public void Add(string file, int mesh, int slot, int triangleCount)
        {
            _rooms.Add(file);
            TriangleCount += triangleCount;
            _uses.Add(new ContactCodeSample(file, mesh, slot, triangleCount));
        }
        public ContactCodeProfile ToReport() => new(
            Code,
            checked((int)(Code & 0x3F)),
            _uses.Count,
            _rooms.Count,
            TriangleCount,
            _uses.OrderByDescending(item => item.TriangleCount)
                .ThenBy(item => item.File, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.Mesh)
                .ThenBy(item => item.Slot)
                .Take(32)
                .ToArray());
    }
    private sealed record SurfaceLayerMeshManifest(int MeshIndex, int MeshOffset, int VertexCount, int TriangleCount);
    private sealed record SurfaceLayerManifest(ushort SurfaceId, int TriangleCount, IReadOnlyList<SurfaceLayerMeshManifest> Meshes);
    private sealed record SurfaceLayersManifest(string Name, string CoordinateSpace, IReadOnlyList<SurfaceLayerManifest> Layers);
    private sealed record FloorPatchCandidate(int MeshIndex, int SurfaceIdsOffset, int TriangleIndex, Vec3 Center);
    private sealed record FloorPatchTriangle(int MeshIndex, int TriangleIndex, Vec3 Center);
    private sealed record FloorSurfaceTestOutput(ushort SurfaceId, string File);
    private sealed record FloorSurfaceTestManifest(string Source, string Name, string Guarantee, IReadOnlyList<FloorPatchTriangle> PatchTriangles, IReadOnlyList<FloorSurfaceTestOutput> Outputs);
    private sealed record Vec3(float X, float Y, float Z);
    private sealed record Vec2(float X, float Y);
    private sealed record Triangle(int A, int B, int C);
    private sealed record CollisionMesh(
        int Index,
        int Offset,
        int DescriptorOffset,
        uint PayloadLength,
        int MaterialCodesOffset,
        IReadOnlyList<uint> MaterialCodes,
        uint HeaderField0,
        uint HeaderField1,
        uint SerializationFlags,
        IReadOnlyList<Vec3> Vertices,
        IReadOnlyList<Triangle> Triangles,
        IReadOnlyList<ushort> SurfaceIds,
        int SurfaceIdsOffset,
        uint RtreePreludeFirstWord,
        int RtreePreludeLength,
        int RtreeOffset,
        uint TailWord,
        float GeomEpsilon,
        IReadOnlyList<byte> ExtraFlags,
        IReadOnlyList<ushort> FaceRemaps);
    private sealed record CidbGeometry(string Name, int CidbSize, IReadOnlyList<CollisionMesh> Meshes);
    private sealed record Bounds3(Vec3 Min, Vec3 Max);
    private sealed record MeshManifest(
        int Index,
        int Offset,
        int DescriptorOffset,
        uint PayloadLength,
        int MaterialCodesOffset,
        IReadOnlyList<uint> MaterialCodes,
        uint HeaderField0,
        uint HeaderField1,
        uint SerializationFlags,
        int VertexCount,
        int TriangleCount,
        Bounds3 Bounds,
        IReadOnlyList<ushort> TriangleSurfaceIds,
        uint RtreePreludeFirstWord,
        int RtreePreludeLength,
        int RtreeOffset,
        float GeomEpsilon);
    private sealed record GeometryManifest(string Name, int CidbSize, int MeshCount, int TotalVertices, int TotalTriangles, IReadOnlyList<MeshManifest> Meshes)
    {
        public string RawCoordinates = "CIDB raw: (x, y, z); X/Z are the floor plane and Y is the vertical axis. The Blender companion maps Y to negative Blender Z.";
        public string BlenderDaeCoordinates = "Switch Toolbox DAE companion OBJ: rotate -90 degrees around X; (x, y, z) -> (x, z, -y).";
        public string Editing = "rebuild-geometry supports same-topology vertex and surface-ID edits and refits the embedded PhysX RTRE tree. Adding/removing/reordering vertices or faces is not supported.";
        public string SurfaceBehavior = "TriangleSurfaceIds are per-triangle u16 material-table indices. MaterialCodes is the matching per-mesh u32 contact/behaviour table serialized immediately after NXS/MESH; MaterialCodes[TriangleSurfaceIds[triangle]] is the runtime contact code used by PlayerLink footsteps/effects.";
    }

    private enum Projection { Top, Elevation }
    private sealed record CidbContainer(byte[] OriginalData, int CidbOffset, byte[] CidbData);
    private sealed record ObjFace(int A, int B, int C, ushort SurfaceId, byte ExtraFlag);
    private sealed record ObjObject(string Name, List<ObjFace> Faces);
    private sealed record ObjDocument(IReadOnlyList<Vec3> Vertices, IReadOnlyList<ObjObject> Objects, bool IsBlenderCompanion);
    private sealed record MeshEdits(IReadOnlyList<Vec3> Vertices, IReadOnlyList<ushort> SurfaceIds);
    private sealed record CookFace(int A, int B, int C, ushort SurfaceId, byte ExtraFlag, ushort? FaceRemap, int SourceGroup, uint? ContactCode = null);
    private sealed record CookInput(IReadOnlyList<Vec3> Vertices, IReadOnlyList<CookFace> Faces, IReadOnlyDictionary<int, VertexRange> SourceGroupRanges, IReadOnlyList<PrimitiveSpec>? Primitives = null);
    // Authored primitive section (game coordinates). Shape 1 = box (Size = half-extents xyz), 3 = sphere (Size = radius).
    private sealed record PrimitiveSpec(int Shape, int Group, Vec3 Position, Vec3 Rotation, IReadOnlyList<float> Size, IReadOnlyList<uint> Codes);
    private sealed record VertexRange(int Start, int Count);
    private sealed record CookMesh(IReadOnlyList<Vec3> Vertices, IReadOnlyList<Triangle> Triangles, IReadOnlyList<ushort> SurfaceIds, IReadOnlyList<byte> ExtraFlags, IReadOnlyList<ushort> FaceRemaps, IReadOnlyList<uint?>? ContactCodes = null);
    private sealed record ResolvedCookMesh(CookMesh Mesh, IReadOnlyList<uint> MaterialCodes);
    private sealed record MaterialKey(ushort SurfaceId, byte ExtraFlag, ushort? FaceRemap = null, uint? ContactCode = null);
    private sealed class PhysxNode
    {
        public Bounds3 Bounds = new(new Vec3(float.MaxValue, float.MaxValue, float.MaxValue), new Vec3(-float.MaxValue, -float.MaxValue, -float.MaxValue));
        public int LeafStart = -1;
        public int LeafCount = -1;
        public int ChildStart = -1;
    }
    private sealed record PhysxSplit(int Start, int Count)
    {
        public IEnumerable<int> Indices(IReadOnlyList<int> order) => order.Skip(Start).Take(Count);
    }
    private sealed class PhysxSahContext
    {
        public IReadOnlyList<Bounds3> Bounds { get; }
        private readonly int[][] _orders = new int[3][];
        private readonly int[][] _ranks = new int[3][];
        public PhysxSahContext(IReadOnlyList<Bounds3> bounds)
        {
            Bounds = bounds;
            for (var axis = 0; axis < 3; axis++)
            {
                var axisCopy = axis;
                var sorted = Enumerable.Range(0, bounds.Count).ToArray();
                PhysxSort(sorted, (left, right) => Center(bounds[left], axisCopy) < Center(bounds[right], axisCopy));
                _orders[axis] = sorted;
                var ranks = new int[bounds.Count];
                for (var index = 0; index < sorted.Length; index++) ranks[sorted[index]] = index;
                _ranks[axis] = ranks;
            }
        }
        public int[] SortedSubset(IReadOnlyList<int> currentOrder, PhysxSplit interval, int axis)
        {
            if (interval.Start == 0 && interval.Count == Bounds.Count)
                return _orders[axis].ToArray();
            var ranks = interval.Indices(currentOrder).Select(index => _ranks[axis][index]).ToArray();
            PhysxSort(ranks, (left, right) => left < right);
            return ranks.Select(rank => _orders[axis][rank]).ToArray();
        }
    }
    private sealed record BuildNode(Bounds3 Bounds, uint Pointer);
    private sealed class BuildPage { public BuildNode?[] Nodes { get; } = new BuildNode?[4]; }
    private sealed record BuildPageResult(int PageIndex, Bounds3 Bounds, int Level);
}
