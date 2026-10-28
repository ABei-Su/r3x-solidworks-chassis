using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            ISldWorks app;
            try
            {
                app = (ISldWorks)Marshal.GetActiveObject("SldWorks.Application");
            }
            catch (COMException)
            {
                Type type = Type.GetTypeFromProgID("SldWorks.Application", true);
                app = (ISldWorks)Activator.CreateInstance(type);
            }

            app.Visible = true;
            if (args.Length == 3 &&
                (args[0] == "import-inspect" || args[0] == "build-chassis"))
            {
                Import(app, Path.GetFullPath(args[1]), Path.GetFullPath(args[2]));
                return 0;
            }
            if (args.Length == 1 && args[0] == "inspect-active")
            {
                InspectActive(app);
                return 0;
            }
            if (args.Length == 1 && args[0] == "verify-active")
            {
                VerifyActive(app);
                return 0;
            }
            if (args.Length == 1 && args[0] == "cut-one-circle")
            {
                CutOneCircle(app);
                return 0;
            }
            if (args.Length == 1 && args[0] == "cut-all-circles")
            {
                CutAllCircles(app);
                return 0;
            }
            if (args.Length == 1 && args[0] == "cut-circle-batches")
            {
                CutCircleBatches(app);
                return 0;
            }
            if (args.Length == 1 && args[0] == "cleanup-small-bodies")
            {
                CleanupSmallBodies(app);
                return 0;
            }
            if (args.Length == 2 && args[0] == "save-preview")
            {
                SavePreview(app, Path.GetFullPath(args[1]));
                return 0;
            }
            IModelDoc2 active = app.IActiveDoc2;
            Console.WriteLine("CONNECTED=true");
            Console.WriteLine("REVISION=" + app.RevisionNumber());
            Console.WriteLine("ACTIVE=" + (active == null ? "" : active.GetPathName()));
            Console.WriteLine("PART_TEMPLATE=" + app.GetUserPreferenceStringValue(8));
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.ToString());
            return 1;
        }
    }

    private static void Import(ISldWorks sw, string dwg, string output)
    {
        if (!File.Exists(dwg)) throw new FileNotFoundException("DWG missing", dwg);
        Directory.CreateDirectory(Path.GetDirectoryName(output));
        IImportDxfDwgData data = (IImportDxfDwgData)sw.GetImportFileData(dwg);
        data.set_ImportMethod("", (int)swImportDxfDwg_ImportMethod_e.swImportDxfDwg_ImportToPartSketch);
        data.set_LengthUnit("", (int)swLengthUnit_e.swMM);
        data.set_AddSketchConstraints("", false);
        data.set_ImportDimensions("", false);
        data.set_ImportHatch("", false);
        data.SetMergePoints("", true, 0.000002);
        data.SetPosition("", (int)swDwgImportEntitiesPositioning_e.swDwgEntitiesCentered, 0, 0);
        int importErrors = 0;
        IModelDoc2 doc = sw.LoadFile4(dwg, "", data, ref importErrors);
        if (doc == null) throw new InvalidOperationException("DWG import failed: " + importErrors);
        doc.ViewZoomtofit2();
        doc.GraphicsRedraw2();

        IFeature sketchFeature = null;
        ISketch sketch = null;
        for (IFeature f = doc.IFirstFeature(); f != null; f = f.IGetNextFeature())
        {
            ISketch candidate = f.GetSpecificFeature2() as ISketch;
            object[] candidateSegments = candidate == null ? null : candidate.GetSketchSegments() as object[];
            if (candidateSegments != null && candidateSegments.Length > 0)
            {
                sketchFeature = f;
                sketch = candidate;
                break;
            }
        }
        if (sketch == null) throw new InvalidOperationException("No imported sketch found");

        object[] segments = sketch.GetSketchSegments() as object[] ?? new object[0];
        object[] texts = sketch.GetSketchTextSegments() as object[] ?? new object[0];
        var groups = new Dictionary<string, Box>();
        foreach (object o in segments)
        {
            ISketchSegment s = o as ISketchSegment;
            if (s == null) continue;
            string key = string.Format(CultureInfo.InvariantCulture,
                "layer={0};color={1};type={2};style={3};width={4};construction={5}",
                s.Layer ?? "", s.Color, s.GetType(), s.Style, s.Width, s.ConstructionGeometry);
            Box b;
            if (!groups.TryGetValue(key, out b)) { b = new Box(); groups.Add(key, b); }
            b.Count++;
            AddBounds(b, s);
        }

        Box plate = new Box();
        foreach (object o in segments)
        {
            ISketchSegment s = o as ISketchSegment;
            if (s != null && s.GetType() == (int)swSketchSegments_e.swSketchARC &&
                s.Style == 0 && !s.ConstructionGeometry) AddBounds(plate, s);
        }
        double centerX = (plate.MinX + plate.MaxX) / 2.0;
        double centerY = (plate.MinY + plate.MaxY) / 2.0;
        var physical = new List<Geometry>();
        foreach (object o in segments)
        {
            ISketchSegment s = o as ISketchSegment;
            if (s != null && s.Style == 0 && !s.ConstructionGeometry &&
                (s.GetType() == (int)swSketchSegments_e.swSketchLINE ||
                 s.GetType() == (int)swSketchSegments_e.swSketchARC) &&
                IsInside(s, plate, 0.000001))
                physical.Add(Geometry.From(s, centerX, centerY));
        }
        Console.WriteLine("RAW_PHYSICAL_SEGMENTS=" + physical.Count);
        physical = ClosedOnly(physical);
        List<Geometry> outer = LargestComponent(physical);
        Console.WriteLine("OUTER_SEGMENTS=" + outer.Count);
        doc = CreateCleanPart(sw, outer, physical);

        int saveErrors = 0, saveWarnings = 0;
        bool saved = doc.SaveAs4(output, 0, (int)swSaveAsOptions_e.swSaveAsOptions_Silent,
            ref saveErrors, ref saveWarnings);
        Console.WriteLine("IMPORT_ERRORS=" + importErrors);
        Console.WriteLine("SKETCH_FEATURE=" + sketchFeature.Name);
        Console.WriteLine("SEGMENTS=" + segments.Length);
        Console.WriteLine("TEXT_SEGMENTS=" + texts.Length);
        Console.WriteLine("CONTOURS=" + sketch.GetSketchContourCount());
        Console.WriteLine("REGIONS=" + sketch.GetSketchRegionCount());
        foreach (KeyValuePair<string, Box> p in groups.OrderByDescending(x => x.Value.Count))
        {
            Box b = p.Value;
            Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "GROUP {0};count={1};bbox_mm=[{2:F3},{3:F3}]-[{4:F3},{5:F3}]",
                p.Key, b.Count, b.MinX * 1000, b.MinY * 1000, b.MaxX * 1000, b.MaxY * 1000));
        }
        Console.WriteLine("SAVED=" + saved);
        Console.WriteLine("SAVE_ERRORS=" + saveErrors);
        Console.WriteLine("SAVE_WARNINGS=" + saveWarnings);
        Console.WriteLine("OUTPUT=" + output);
    }

    private static void CutCircleRecursive(IModelDoc2 doc, IFeature plane,
        List<Geometry> circles, ref int cutCircles, ref int skipped, ref int features)
    {
        if (TryCircleCut(doc, plane, circles))
        {
            cutCircles += circles.Count;
            features++;
            return;
        }
        if (circles.Count == 1) { skipped++; return; }
        int middle = circles.Count / 2;
        CutCircleRecursive(doc, plane, circles.Take(middle).ToList(),
            ref cutCircles, ref skipped, ref features);
        CutCircleRecursive(doc, plane, circles.Skip(middle).ToList(),
            ref cutCircles, ref skipped, ref features);
    }

    private static bool TryCircleCut(IModelDoc2 doc, IFeature plane,
        List<Geometry> circles)
    {
        doc.ClearSelection2(true);
        IFeature currentPlane = null;
        for (IFeature f = doc.IFirstFeature(); f != null; f = f.IGetNextFeature())
            if (f.GetTypeName2() == "RefPlane") { currentPlane = f; break; }
        if (currentPlane == null || !currentPlane.Select2(false, 0)) return false;
        ISketchManager manager = doc.SketchManager;
        manager.InsertSketch(true);
        foreach (Geometry g in circles)
            manager.CreateCircleByRadius(g.CX, g.CY, 0, g.Radius);
        manager.InsertSketch(true);
        doc.ForceRebuild3(false);
        doc.ClearSelection2(true);
        IFeature profile = null;
        for (IFeature f = doc.IFirstFeature(); f != null; f = f.IGetNextFeature())
            if (f.GetTypeName2() == "ProfileFeature") profile = f;
        if (profile == null || !profile.Select2(false, 0)) return false;
        IFeature cut = doc.FeatureManager.FeatureCut4(
            true, false, true, 1, 1, 0.01, 0.01,
            false, false, false, false, 0, 0,
            false, false, false, false, false, true, true,
            false, false, false, 0, 0, false, true);
        if (cut != null) return true;
        doc.ClearSelection2(true);
        profile.Select2(false, 0);
        doc.EditDelete();
        return false;
    }

    private static void InspectActive(ISldWorks sw)
    {
        IModelDoc2 doc = sw.IActiveDoc2;
        if (doc == null) throw new InvalidOperationException("No active document");
        ISketch sketch = null;
        IFeature found = null;
        for (IFeature f = doc.IFirstFeature(); f != null; f = f.IGetNextFeature())
        {
            ISketch s = f.GetSpecificFeature2() as ISketch;
            object[] a = s == null ? null : s.GetSketchSegments() as object[];
            if (a != null && a.Length > 0) { sketch = s; found = f; }
        }
        if (sketch == null) throw new InvalidOperationException("No populated sketch");
        var list = (sketch.GetSketchSegments() as object[] ?? new object[0])
            .OfType<ISketchSegment>().OrderBy(s => s.GetLength()).ToList();
        int open = 0, closed = 0;
        Console.WriteLine("ACTIVE=" + doc.GetPathName());
        Console.WriteLine("SKETCH=" + found.Name);
        Console.WriteLine("CHECK=" + sketch.CheckFeatureUse(1, ref open, ref closed));
        Console.WriteLine("SEGMENTS=" + list.Count);
        var duplicates = list.GroupBy(SegmentKey)
            .Where(g => g.Count() > 1).OrderByDescending(g => g.Count()).ToList();
        Console.WriteLine("DUPLICATE_GROUPS=" + duplicates.Count);
        Console.WriteLine("DUPLICATE_EXTRA_SEGMENTS=" +
            duplicates.Sum(g => g.Count() - 1));
        foreach (var group in duplicates.Take(20))
            Console.WriteLine("DUP count=" + group.Count() + ";key=" + group.Key);
        foreach (ISketchSegment s in list.Take(30))
        {
            string extra = "";
            if (s.GetType() == (int)swSketchSegments_e.swSketchARC)
            {
                ISketchArc a = (ISketchArc)s;
                extra = string.Format(CultureInfo.InvariantCulture,
                    ";circle={0};radius_mm={1:G9}", a.IsCircle(), a.GetRadius() * 1000);
            }
            Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "SHORT type={0};length_mm={1:G9}{2}", s.GetType(), s.GetLength() * 1000, extra));
        }
    }

    private static void VerifyActive(ISldWorks sw)
    {
        IModelDoc2 doc = sw.IActiveDoc2;
        if (doc == null) throw new InvalidOperationException("No active document");
        Console.WriteLine("ACTIVE=" + doc.GetPathName());
        Console.WriteLine("SAVE_FLAG=" + doc.GetSaveFlag());
        object[] bodies = ((IPartDoc)doc).GetBodies2((int)swBodyType_e.swSolidBody, false) as object[];
        Console.WriteLine("SOLID_BODIES=" + (bodies == null ? 0 : bodies.Length));
        if (bodies != null)
        {
            foreach (object item in bodies)
            {
                IBody2 body = (IBody2)item;
                double[] box = body.GetBodyBox() as double[];
                double[] mass = body.GetMassProperties(1.0) as double[];
                Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                    "BODY name={0};volume_mm3={1:G10};bbox_mm=[{2:G8},{3:G8},{4:G8}]-[{5:G8},{6:G8},{7:G8}]",
                    body.Name, mass == null ? 0 : mass[3] * 1e9,
                    box[0] * 1000, box[1] * 1000, box[2] * 1000,
                    box[3] * 1000, box[4] * 1000, box[5] * 1000));
            }
        }
        for (IFeature f = doc.IFirstFeature(); f != null; f = f.IGetNextFeature())
            Console.WriteLine("FEATURE type=" + f.GetTypeName2() + ";name=" + f.Name);
    }

    private static void CutOneCircle(ISldWorks sw)
    {
        IModelDoc2 doc = sw.IActiveDoc2;
        if (doc == null) throw new InvalidOperationException("No active document");
        ISketchArc source = null;
        IFeature plane = null;
        for (IFeature f = doc.IFirstFeature(); f != null; f = f.IGetNextFeature())
        {
            if (plane == null && f.GetTypeName2() == "RefPlane") plane = f;
            ISketch sketch = f.GetSpecificFeature2() as ISketch;
            foreach (object o in sketch == null ? new object[0] :
                     sketch.GetSketchSegments() as object[] ?? new object[0])
            {
                ISketchArc arc = o as ISketchArc;
                if (arc != null && arc.IsCircle() != 0) { source = arc; break; }
            }
            if (source != null && plane != null) break;
        }
        if (source == null || plane == null) throw new InvalidOperationException("Circle or plane missing");
        ISketchPoint c = source.IGetCenterPoint2();
        double r = source.GetRadius();
        doc.ClearSelection2(true);
        plane.Select2(false, 0);
        ISketchManager manager = doc.SketchManager;
        manager.InsertSketch(true);
        manager.CreateCircleByRadius(c.X, c.Y, 0, r);
        manager.InsertSketch(true);
        doc.ClearSelection2(true);
        IFeature profile = null;
        for (IFeature f = doc.IFirstFeature(); f != null; f = f.IGetNextFeature())
            if (f.GetTypeName2() == "ProfileFeature") profile = f;
        if (profile == null || !profile.Select2(false, 0))
            throw new InvalidOperationException("Cut sketch selection failed");
        IFeature cut = doc.FeatureManager.FeatureCut4(
            true, false, true, 1, 1, 0.01, 0.01,
            false, false, false, false, 0, 0,
            false, false, false, false, false, true, true,
            false, false, false, 0, 0, false, true);
        Console.WriteLine("ONE_CIRCLE_CUT=" + (cut != null));
        Console.WriteLine("SAVE_RESULT=" + doc.Save2(true));
    }

    private static void CutAllCircles(ISldWorks sw)
    {
        IModelDoc2 doc = sw.IActiveDoc2;
        if (doc == null) throw new InvalidOperationException("No active document");
        IFeature best = null;
        int bestCount = 0;
        for (IFeature f = doc.IFirstFeature(); f != null; f = f.IGetNextFeature())
        {
            ISketch sketch = f.GetSpecificFeature2() as ISketch;
            int count = 0;
            foreach (object o in sketch == null ? new object[0] :
                     sketch.GetSketchSegments() as object[] ?? new object[0])
            {
                ISketchArc arc = o as ISketchArc;
                if (arc != null && arc.IsCircle() != 0) count++;
            }
            if (count > bestCount) { best = f; bestCount = count; }
        }
        if (best == null) throw new InvalidOperationException("No circle sketch found");
        doc.ClearSelection2(true);
        if (!best.Select2(false, 0)) throw new InvalidOperationException("Circle sketch selection failed");
        IFeature cut = doc.FeatureManager.FeatureCut4(
            true, false, true, 1, 1, 0.01, 0.01,
            false, false, false, false, 0, 0,
            false, false, false, false, false, true, true,
            false, false, false, 0, 0, false, true);
        Console.WriteLine("ALL_CIRCLES=" + bestCount);
        Console.WriteLine("ALL_CIRCLE_CUT=" + (cut != null));
        Console.WriteLine("SAVE_RESULT=" + doc.Save2(true));
    }

    private static void CutCircleBatches(ISldWorks sw)
    {
        IModelDoc2 doc = sw.IActiveDoc2;
        if (doc == null) throw new InvalidOperationException("No active document");
        IFeature plane = null, sourceFeature = null;
        var circles = new List<Geometry>();
        int bestCount = 0;
        for (IFeature f = doc.IFirstFeature(); f != null; f = f.IGetNextFeature())
        {
            if (plane == null && f.GetTypeName2() == "RefPlane") plane = f;
            ISketch sketch = f.GetSpecificFeature2() as ISketch;
            var found = new List<Geometry>();
            foreach (object o in sketch == null ? new object[0] :
                     sketch.GetSketchSegments() as object[] ?? new object[0])
            {
                ISketchArc arc = o as ISketchArc;
                if (arc == null || arc.IsCircle() == 0) continue;
                ISketchPoint c = arc.IGetCenterPoint2();
                found.Add(new Geometry {
                    Type = (int)swSketchSegments_e.swSketchARC, IsCircle = 1,
                    CX = c.X, CY = c.Y, Radius = arc.GetRadius()
                });
            }
            if (found.Count > bestCount)
            {
                bestCount = found.Count;
                circles = found;
                sourceFeature = f;
            }
        }
        if (plane == null || sourceFeature == null) throw new InvalidOperationException("Circle source missing");
        int cutCircles = 0, skipped = 0, features = 0;
        for (int start = 0; start < circles.Count; start += 20)
        {
            List<Geometry> batch = circles.Skip(start).Take(20).ToList();
            CutCircleRecursive(doc, plane, batch, ref cutCircles, ref skipped, ref features);
            Console.WriteLine("BATCH_PROGRESS=" + Math.Min(start + 20, circles.Count) + "/" + circles.Count);
        }
        Console.WriteLine("CUT_CIRCLES=" + cutCircles);
        Console.WriteLine("SKIPPED_CIRCLES=" + skipped);
        Console.WriteLine("CUT_FEATURES=" + features);
        Console.WriteLine("SAVE_RESULT=" + doc.Save2(true));
    }

    private static void CleanupSmallBodies(ISldWorks sw)
    {
        IModelDoc2 doc = sw.IActiveDoc2;
        if (doc == null) throw new InvalidOperationException("No active document");
        object[] raw = ((IPartDoc)doc).GetBodies2((int)swBodyType_e.swSolidBody, false) as object[];
        if (raw == null || raw.Length <= 1) { Console.WriteLine("CLEANUP_NEEDED=false"); return; }
        var bodies = raw.Cast<IBody2>().Select(b => new {
            Body = b, Mass = b.GetMassProperties(1.0) as double[]
        }).OrderByDescending(x => x.Mass[3]).ToList();
        doc.ClearSelection2(true);
        for (int i = 1; i < bodies.Count; i++) bodies[i].Body.Select(true, 0);
        IFeature cleanup = doc.FeatureManager.InsertDeleteBody2(false);
        Console.WriteLine("DELETED_SMALL_BODIES=" + (bodies.Count - 1));
        Console.WriteLine("CLEANUP_FEATURE=" + (cleanup != null));
        Console.WriteLine("SAVE_RESULT=" + doc.Save2(true));
        object[] remaining = ((IPartDoc)doc).GetBodies2((int)swBodyType_e.swSolidBody, false) as object[];
        Console.WriteLine("REMAINING_BODIES=" + (remaining == null ? 0 : remaining.Length));
    }

    private static void SavePreview(ISldWorks sw, string path)
    {
        IModelDoc2 doc = sw.IActiveDoc2;
        if (doc == null) throw new InvalidOperationException("No active document");
        doc.ShowNamedView2("*Isometric", (int)swStandardViews_e.swIsometricView);
        doc.ViewZoomtofit2();
        doc.GraphicsRedraw2();
        Console.WriteLine("PREVIEW_SAVED=" + doc.SaveBMP(path, 1600, 1200));
        Console.WriteLine("PREVIEW=" + path);
    }

    private static string SegmentKey(ISketchSegment s)
    {
        if (s.GetType() == (int)swSketchSegments_e.swSketchLINE)
        {
            ISketchLine line = (ISketchLine)s;
            ISketchPoint a = line.IGetStartPoint2(), b = line.IGetEndPoint2();
            string p1 = PointKey(a.X, a.Y), p2 = PointKey(b.X, b.Y);
            return "L:" + (string.CompareOrdinal(p1, p2) <= 0 ? p1 + ":" + p2 : p2 + ":" + p1);
        }
        ISketchArc arc = (ISketchArc)s;
        ISketchPoint c = arc.IGetCenterPoint2();
        if (arc.IsCircle() != 0)
            return "C:" + PointKey(c.X, c.Y) + ":" + Math.Round(arc.GetRadius() / 0.000005);
        ISketchPoint a1 = arc.IGetStartPoint2(), a2 = arc.IGetEndPoint2();
        string e1 = PointKey(a1.X, a1.Y), e2 = PointKey(a2.X, a2.Y);
        string ends = string.CompareOrdinal(e1, e2) <= 0 ? e1 + ":" + e2 : e2 + ":" + e1;
        return "A:" + PointKey(c.X, c.Y) + ":" +
            Math.Round(arc.GetRadius() / 0.000005) + ":" + ends;
    }

    private static List<Geometry> LargestComponent(List<Geometry> items)
    {
        var endpoints = new Dictionary<string, List<int>>();
        for (int i = 0; i < items.Count; i++)
        {
            Geometry g = items[i];
            if (g.Type == (int)swSketchSegments_e.swSketchARC && g.IsCircle != 0) continue;
            AddEndpoint(endpoints, PointKey(g.X1, g.Y1), i);
            AddEndpoint(endpoints, PointKey(g.X2, g.Y2), i);
        }
        var visited = new HashSet<int>();
        List<int> best = null;
        double bestScore = -1;
        for (int seed = 0; seed < items.Count; seed++)
        {
            Geometry seedGeometry = items[seed];
            if ((seedGeometry.Type == (int)swSketchSegments_e.swSketchARC &&
                 seedGeometry.IsCircle != 0) || visited.Contains(seed)) continue;
            var component = new List<int>();
            var queue = new Queue<int>();
            queue.Enqueue(seed);
            visited.Add(seed);
            while (queue.Count > 0)
            {
                int i = queue.Dequeue();
                component.Add(i);
                Geometry g = items[i];
                foreach (string key in new[] { PointKey(g.X1, g.Y1), PointKey(g.X2, g.Y2) })
                {
                    foreach (int next in endpoints[key])
                        if (!visited.Contains(next)) { visited.Add(next); queue.Enqueue(next); }
                }
            }
            double minX = double.PositiveInfinity, minY = double.PositiveInfinity;
            double maxX = double.NegativeInfinity, maxY = double.NegativeInfinity;
            foreach (int i in component)
            {
                Geometry g = items[i];
                minX = Math.Min(minX, Math.Min(g.X1, g.X2));
                minY = Math.Min(minY, Math.Min(g.Y1, g.Y2));
                maxX = Math.Max(maxX, Math.Max(g.X1, g.X2));
                maxY = Math.Max(maxY, Math.Max(g.Y1, g.Y2));
                if (g.Type == (int)swSketchSegments_e.swSketchARC)
                {
                    minX = Math.Min(minX, g.CX - g.Radius);
                    minY = Math.Min(minY, g.CY - g.Radius);
                    maxX = Math.Max(maxX, g.CX + g.Radius);
                    maxY = Math.Max(maxY, g.CY + g.Radius);
                }
            }
            double score = (maxX - minX) * (maxY - minY);
            if (score > bestScore) { bestScore = score; best = component; }
        }
        if (best == null) throw new InvalidOperationException("No chassis outer contour found");
        return best.Select(i => items[i]).ToList();
    }

    private static List<Geometry> ClosedOnly(List<Geometry> items)
    {
        var endpoints = new Dictionary<string, List<int>>();
        var keep = new HashSet<int>();
        var ignored = new HashSet<int>();
        for (int i = 0; i < items.Count; i++)
        {
            Geometry g = items[i];
            if (g.Type == (int)swSketchSegments_e.swSketchARC && g.IsCircle != 0)
            {
                keep.Add(i);
                continue;
            }
            if (PointKey(g.X1, g.Y1) == PointKey(g.X2, g.Y2))
            {
                ignored.Add(i);
                continue;
            }
            AddEndpoint(endpoints, PointKey(g.X1, g.Y1), i);
            AddEndpoint(endpoints, PointKey(g.X2, g.Y2), i);
        }

        var visited = new HashSet<int>();
        for (int seed = 0; seed < items.Count; seed++)
        {
            if (keep.Contains(seed) || ignored.Contains(seed) || visited.Contains(seed)) continue;
            var component = new List<int>();
            var queue = new Queue<int>();
            queue.Enqueue(seed);
            visited.Add(seed);
            while (queue.Count > 0)
            {
                int i = queue.Dequeue();
                component.Add(i);
                Geometry g = items[i];
                foreach (string key in new[] { PointKey(g.X1, g.Y1), PointKey(g.X2, g.Y2) })
                {
                    List<int> linked;
                    if (!endpoints.TryGetValue(key, out linked)) continue;
                    foreach (int next in linked)
                        if (!visited.Contains(next)) { visited.Add(next); queue.Enqueue(next); }
                }
            }

            bool closed = true;
            foreach (int i in component)
            {
                Geometry g = items[i];
                if (endpoints[PointKey(g.X1, g.Y1)].Count != 2 ||
                    endpoints[PointKey(g.X2, g.Y2)].Count != 2)
                { closed = false; break; }
            }
            if (closed) foreach (int i in component) keep.Add(i);
        }

        var result = new List<Geometry>();
        for (int i = 0; i < items.Count; i++) if (keep.Contains(i)) result.Add(items[i]);
        Console.WriteLine("CLOSED_ONLY_SEGMENTS=" + result.Count);
        Console.WriteLine("DISCARDED_OPEN_OR_BRANCH=" + (items.Count - result.Count));
        return result;
    }

    private static double Snap(double value)
    {
        const double tolerance = 0.000005;
        return Math.Round(value / tolerance) * tolerance;
    }

    private static string PointKey(double x, double y)
    {
        const double tolerance = 0.000005;
        return Math.Round(x / tolerance).ToString(CultureInfo.InvariantCulture) + "," +
               Math.Round(y / tolerance).ToString(CultureInfo.InvariantCulture);
    }

    private static void AddEndpoint(Dictionary<string, List<int>> map, string key, int index)
    {
        List<int> list;
        if (!map.TryGetValue(key, out list)) { list = new List<int>(); map.Add(key, list); }
        list.Add(index);
    }

    private static void AddSketchPoint(Dictionary<string, List<ISketchPoint>> map,
        string key, ISketchPoint point)
    {
        List<ISketchPoint> list;
        if (!map.TryGetValue(key, out list))
        {
            list = new List<ISketchPoint>();
            map.Add(key, list);
        }
        list.Add(point);
    }

    private static IModelDoc2 CreateCleanPart(ISldWorks sw, List<Geometry> items,
        List<Geometry> allItems)
    {
        string template = sw.GetUserPreferenceStringValue(8);
        IModelDoc2 clean = sw.INewDocument2(template, 0, 0, 0);
        if (clean == null) throw new InvalidOperationException("Could not create clean part");

        IFeature plane = null;
        for (IFeature f = clean.IFirstFeature(); f != null; f = f.IGetNextFeature())
        {
            if (f.GetTypeName2() == "RefPlane") { plane = f; break; }
        }
        if (plane == null || !plane.Select2(false, 0))
            throw new InvalidOperationException("Could not select the first reference plane");

        ISketchManager manager = clean.SketchManager;
        manager.InsertSketch(true);
        manager.AddToDB = true;
        manager.DisplayWhenAdded = false;
        int createFailures = 0;
        foreach (Geometry g in items)
        {
            ISketchSegment created;
            if (g.Type == (int)swSketchSegments_e.swSketchLINE)
                created = manager.CreateLine(g.X1, g.Y1, 0, g.X2, g.Y2, 0);
            else if (g.IsCircle != 0)
                created = manager.CreateCircleByRadius(g.CX, g.CY, 0, g.Radius);
            else
                created = manager.CreateArc(g.CX, g.CY, 0, g.X1, g.Y1, 0,
                    g.X2, g.Y2, 0, (short)g.Direction);
            if (created == null) createFailures++;
        }
        manager.AddToDB = false;
        manager.DisplayWhenAdded = true;
        Console.WriteLine("CREATE_FAILURES=" + createFailures);
        manager.InsertSketch(true);

        clean.ClearSelection2(true);
        IFeature profile = null;
        for (IFeature f = clean.IFirstFeature(); f != null; f = f.IGetNextFeature())
            if (f.GetTypeName2() == "ProfileFeature") profile = f;
        if (profile == null || !profile.Select2(false, 0))
            throw new InvalidOperationException("Could not select rebuilt chassis sketch");

        ISketch rebuilt = profile.GetSpecificFeature2() as ISketch;
        int openCount = 0, closedCount = 0;
        int check = rebuilt.CheckFeatureUse(1, ref openCount, ref closedCount);
        Console.WriteLine("PHYSICAL_SEGMENTS=" + items.Count);
        Console.WriteLine("SKETCH_CHECK=" + check);
        Console.WriteLine("OPEN_CONTOURS=" + openCount);
        Console.WriteLine("CLOSED_CONTOURS=" + closedCount);
        IFeature boss = clean.FeatureManager.FeatureExtrusion3(
            true, false, false, 0, 0, 0.003, 0.003,
            false, false, false, false, 0, 0,
            false, false, false, false, true, true, true, 0, 0, false);
        if (boss == null)
        {
            Console.WriteLine("EXTRUDE=false");
            return clean;
        }
        Console.WriteLine("EXTRUDE=true");
        clean.ClearSelection2(true);
        if (!plane.Select2(false, 0))
            throw new InvalidOperationException("Could not reselect reference plane for holes");
        manager.InsertSketch(true);
        manager.AddToDB = true;
        manager.DisplayWhenAdded = false;
        int circleCount = 0;
        foreach (Geometry g in allItems)
        {
            if (g.Type == (int)swSketchSegments_e.swSketchARC && g.IsCircle != 0)
            {
                if (manager.CreateCircleByRadius(g.CX, g.CY, 0, g.Radius) != null)
                    circleCount++;
            }
        }
        manager.AddToDB = false;
        manager.DisplayWhenAdded = true;
        manager.InsertSketch(true);
        clean.ClearSelection2(true);
        IFeature holeProfile = null;
        for (IFeature f = clean.IFirstFeature(); f != null; f = f.IGetNextFeature())
            if (f.GetTypeName2() == "ProfileFeature") holeProfile = f;
        IFeature cut = null;
        if (holeProfile != null && holeProfile.Select2(false, 0))
            cut = clean.FeatureManager.FeatureCut4(
                true, false, true, 1, 1, 0.01, 0.01,
                false, false, false, false, 0, 0,
                false, false, false, false, false, true, true,
                false, false, false, 0, 0, false, true);
        Console.WriteLine("CIRCLE_HOLES=" + circleCount);
        Console.WriteLine("CIRCLE_CUT=" + (cut != null));
        boss.Name = "底盘板_3mm";
        clean.ShowNamedView2("*Isometric", (int)swStandardViews_e.swIsometricView);
        clean.ViewZoomtofit2();
        clean.GraphicsRedraw2();
        object[] bodies = ((IPartDoc)clean).GetBodies2((int)swBodyType_e.swSolidBody, false) as object[];
        Console.WriteLine("SOLID_BODIES=" + (bodies == null ? 0 : bodies.Length));
        return clean;
    }

    private static bool IsInside(ISketchSegment s, Box plate, double tolerance)
    {
        Box b = new Box();
        AddBounds(b, s);
        return b.MinX >= plate.MinX - tolerance && b.MaxX <= plate.MaxX + tolerance &&
               b.MinY >= plate.MinY - tolerance && b.MaxY <= plate.MaxY + tolerance;
    }

    private static void AddBounds(Box b, ISketchSegment s)
    {
        if (s.GetType() == (int)swSketchSegments_e.swSketchLINE)
        {
            ISketchLine line = (ISketchLine)s;
            AddPoint(b, line.IGetStartPoint2());
            AddPoint(b, line.IGetEndPoint2());
        }
        else if (s.GetType() == (int)swSketchSegments_e.swSketchARC)
        {
            ISketchArc arc = (ISketchArc)s;
            ISketchPoint c = arc.IGetCenterPoint2();
            double r = arc.GetRadius();
            AddPoint(b, c.X - r, c.Y - r);
            AddPoint(b, c.X + r, c.Y + r);
        }
    }

    private static void AddPoint(Box b, ISketchPoint p) { AddPoint(b, p.X, p.Y); }

    private static void AddPoint(Box b, double x, double y)
    {
        b.MinX = Math.Min(b.MinX, x);
        b.MinY = Math.Min(b.MinY, y);
        b.MaxX = Math.Max(b.MaxX, x);
        b.MaxY = Math.Max(b.MaxY, y);
    }

    private sealed class Geometry
    {
        public int Type;
        public double X1, Y1, X2, Y2, CX, CY, Radius;
        public int IsCircle, Direction;

        public static Geometry From(ISketchSegment s, double centerX, double centerY)
        {
            var g = new Geometry { Type = s.GetType() };
            if (g.Type == (int)swSketchSegments_e.swSketchLINE)
            {
                ISketchLine line = (ISketchLine)s;
                ISketchPoint a = line.IGetStartPoint2();
                ISketchPoint b = line.IGetEndPoint2();
                g.X1 = Snap(a.X - centerX); g.Y1 = Snap(a.Y - centerY);
                g.X2 = Snap(b.X - centerX); g.Y2 = Snap(b.Y - centerY);
            }
            else
            {
                ISketchArc arc = (ISketchArc)s;
                ISketchPoint c = arc.IGetCenterPoint2();
                ISketchPoint a = arc.IGetStartPoint2();
                ISketchPoint b = arc.IGetEndPoint2();
                double originalCX = c.X - centerX;
                double originalCY = c.Y - centerY;
                g.CX = originalCX; g.CY = originalCY;
                g.X1 = Snap(a.X - centerX); g.Y1 = Snap(a.Y - centerY);
                g.X2 = Snap(b.X - centerX); g.Y2 = Snap(b.Y - centerY);
                g.Radius = arc.GetRadius();
                g.IsCircle = arc.IsCircle();
                g.Direction = arc.GetRotationDir();
                if (g.IsCircle == 0) AdjustCenter(g, originalCX, originalCY);
            }
            return g;
        }

        private static void AdjustCenter(Geometry g, double originalX, double originalY)
        {
            double dx = g.X2 - g.X1, dy = g.Y2 - g.Y1;
            double chord = Math.Sqrt(dx * dx + dy * dy);
            if (chord <= 1e-12 || chord > 2 * g.Radius) return;
            double mx = (g.X1 + g.X2) / 2, my = (g.Y1 + g.Y2) / 2;
            double h = Math.Sqrt(Math.Max(0, g.Radius * g.Radius - chord * chord / 4));
            double px = -dy / chord, py = dx / chord;
            double c1x = mx + h * px, c1y = my + h * py;
            double c2x = mx - h * px, c2y = my - h * py;
            double d1 = (c1x - originalX) * (c1x - originalX) +
                        (c1y - originalY) * (c1y - originalY);
            double d2 = (c2x - originalX) * (c2x - originalX) +
                        (c2y - originalY) * (c2y - originalY);
            g.CX = d1 <= d2 ? c1x : c2x;
            g.CY = d1 <= d2 ? c1y : c2y;
        }
    }

    private sealed class Box
    {
        public int Count;
        public double MinX = double.PositiveInfinity;
        public double MinY = double.PositiveInfinity;
        public double MaxX = double.NegativeInfinity;
        public double MaxY = double.NegativeInfinity;
    }
}
