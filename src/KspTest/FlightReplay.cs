using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEngine;

namespace KspTest
{
    /// <summary>
    /// Open-loop flight replay, to compare FAR's flight forces between builds without the chaos of free flight.
    /// A recording flies a vessel freely from a saved state and keeps, for every physics frame, each part's
    /// rigidbody pose, velocity and angular velocity as they stood at the start of the frame, and the vessel's
    /// control state. A replay loads the same save and forces those states back at the start of every frame
    /// (TimingManager's ObscenelyEarly stage, before any FixedUpdate that reads them), so FAR's flight code gets the
    /// same inputs whatever forces it computed the frame before; with the same code the outputs must match bit for
    /// bit, and a difference between builds is the code's.
    ///
    /// Both start alike: the save is loaded and the vessel kept packed (OrbitPhysicsManager.HoldVesselUnpack) until
    /// FAR's voxels are built and idle; then frozen at the start (Freeze): the world (WorldState: the clock, the
    /// planets' positions and rotation, the floating origin and Krakensbane frame velocity - a recording captures it
    /// on the first frozen frame) and every part's pose put back each frame, while FAR voxelizes it again there; then
    /// taken off rails by this class at the start of a frame, from that same pose. FAR's wings do nothing while
    /// packed, so each run's FAR state before frame 0 is the freshly loaded one plus voxels from identical inputs.
    /// What KSP derives on render frames is made exact too: the vessel's mass, CoM and velocity are summed again
    /// from the forced parts, and the air FAR reads (density, temperature, pressures, Mach) is recorded and forced.
    ///
    /// Outputs, per frame (at the BetterLateThanNever stage, after FAR's FixedUpdates): FAR's inputs as KSP left
    /// them (clock, density, Mach, speed, position, frame velocity, CoM), each wing's worldSpaceForce and
    /// AerodynamicCenter, stall and control deflection, and each voxel part's worldSpaceAeroForce and
    /// worldSpaceTorque, all as the raw bits in hex; every StateEvery frames the whole FAR part state
    /// (FarStateSnapshot) as text.
    /// </summary>
    public class FlightReplay : MonoBehaviour
    {
        public enum Phase { Off, Holding, Freezing, Starting, Running, Done, Failed }

        public static Phase State { get; private set; } = Phase.Off;
        public static int Frame { get; private set; }
        public static string Error { get; private set; }
        /// <summary>Recording: the inputs for a frame (the state arrives as KSP and the autopilots left it).</summary>
        public static Action<int, FlightCtrlState> Script;
        /// <summary>Called after a frame's outputs are written (the leak test calls the prediction API here).</summary>
        public static Action<int, Vessel> AfterFrame;
        public static int StateEvery = 50;
        /// <summary>Frames on which KSP or FAR changed the vessel's voxel sections (a new voxelization landed).</summary>
        public static readonly List<int> VoxelSwaps = new List<int>();

        private static bool _record;
        private static int _frames;
        private static Recording _rec;
        private static string _recPath, _outPath, _statePath;
        private static StreamWriter _out, _state;
        private static Vessel _v;
        private static Part[] _parts;
        private static object _sections;
        private static object _timing;
        private static FlightReplay _instance;

        private static void Ensure()
        {
            if (_instance != null) return;
            var go = new GameObject("KspTest.FlightReplay");
            DontDestroyOnLoad(go);
            _instance = go.AddComponent<FlightReplay>();
        }

        /// <summary>Records frames from the next Begin into recPath, writing outputs to outPath.</summary>
        public static void PrepareRecord(int frames, string recPath, string outPath, string statePath)
        {
            Ensure();
            _record = true; _frames = frames; _rec = new Recording(); _recPath = recPath;
            Open(outPath, statePath);
        }

        /// <summary>Replays the recording at recPath from the next Begin, writing outputs to outPath.</summary>
        public static void PrepareReplay(string recPath, string outPath, string statePath)
        {
            Ensure();
            _record = false; _rec = Recording.Read(recPath); _frames = _rec.Frames.Count; _recPath = recPath;
            Open(outPath, statePath);
        }

        private static void Open(string outPath, string statePath)
        {
            _outPath = outPath; _statePath = statePath;
            Error = null; Frame = 0; VoxelSwaps.Clear(); BlockedPredictions = 0; _scaledOffset = null;
            SingleThreadVoxels();
            State = Phase.Holding;
            HookTiming();
        }

        /// <summary>
        /// Freezes the held, packed vessel at the start: every frame the world and the parts' poses go back to the
        /// recording's (a recording takes them from the first frozen frame), and FAR is asked to voxelize it there,
        /// so the voxels come from the same poses in every run. Voxelized turns true when that voxelization lands.
        /// </summary>
        public static void Freeze()
        {
            if (State != Phase.Holding) throw new UITestFailure("FlightReplay.Freeze: not holding (" + State + ")");
            _frozen = false; Voxelized = false;
            State = Phase.Freezing;
        }

        // FAR voxelizes a vessel's meshes on several threads at once, into shared voxels: which part a voxel ends up
        // with, and so each part's areas and the sections' part data, depend on how the threads interleave (they
        // differ between runs in the 5th digit). VehicleVoxel.CreateNewVoxel has a single-threaded path; the
        // flight takes the default. This takes that path, in whatever FAR build.
        private static HarmonyLib.Harmony _harmony;

        private static void SingleThreadVoxels()
        {
            if (_harmony != null) return;
            MethodInfo target = Reflect.FindType("FerramAerospaceResearch.FARPartGeometry.VehicleVoxel")?.GetMethod("CreateNewVoxel", BindingFlags.Static | BindingFlags.Public);
            if (target == null || target.GetParameters().All(p => p.Name != "multiThreaded"))
                throw new UITestFailure("FAR's VehicleVoxel.CreateNewVoxel(..., multiThreaded, ...) not found");
            _harmony = new HarmonyLib.Harmony("KspTest.FlightReplay");
            _harmony.Patch(target, new HarmonyLib.HarmonyMethod(typeof(FlightReplay), nameof(VoxelPrefix)));
            Log.Info("FlightReplay: FAR voxelizes single-threaded from now on");
            BlockOtherPredictions();
        }

        private static void VoxelPrefix(ref bool multiThreaded) => multiThreaded = false;

        /// <summary>
        /// While a replay runs, FAR's prediction entry points (FARVesselAero.SimulateAeroPropertiesAt /
        /// SimulateAeroProperties) do nothing unless AllowPredictions is set: other mods ask for predictions on their
        /// own clock (MechJeb's VesselState on render frames), so whatever a prediction leaves behind would land on
        /// different frames in each run. The leak test calls them itself, on fixed frames, with AllowPredictions.
        /// </summary>
        public static bool AllowPredictions;
        public static int BlockedPredictions { get; private set; }

        private static void BlockOtherPredictions()
        {
            Type t = Reflect.FindType("FerramAerospaceResearch.FARAeroComponents.FARVesselAero");
            var targets = t?.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                           .Where(m => m.Name == "SimulateAeroPropertiesAt" || m.Name == "SimulateAeroProperties").ToList();
            if (targets == null || targets.Count == 0) throw new UITestFailure("FAR's FARVesselAero.SimulateAeroProperties(At) not found");
            foreach (MethodInfo m in targets)
                _harmony.Patch(m, new HarmonyLib.HarmonyMethod(typeof(FlightReplay), nameof(PredictionPrefix)));
            Log.Info("FlightReplay: other callers' predictions blocked during replays: " + string.Join(", ", targets.Select(m => m.Name)));
        }

        private static bool PredictionPrefix()
        {
            if (AllowPredictions || State == Phase.Off || State == Phase.Done || State == Phase.Failed) return true;
            BlockedPredictions++;
            return false;
        }

        public static bool Voxelized { get; private set; }
        private static bool _frozen;
        private static object _freezeSections;

        /// <summary>Takes the frozen vessel off rails at the start of the next frame: frame 0.</summary>
        public static void Begin()
        {
            if (State != Phase.Freezing) throw new UITestFailure("FlightReplay.Begin: not frozen (" + State + ")");
            State = Phase.Starting;
        }

        public static void Stop()
        {
            if (_v != null) _v.OnPostAutopilotUpdate -= Inputs;
            _out?.Dispose(); _out = null;
            _state?.Dispose(); _state = null;
            if (State != Phase.Done && State != Phase.Failed) State = Phase.Off;
        }

        public static int FrameCount => _frames;

        private void Update() => HookTiming();

        private void FixedUpdate()
        {
            HookTiming();
            Trace("FlightReplay.FixedUpdate");
            if (State == Phase.Holding || State == Phase.Freezing || State == Phase.Starting)
            {
                Vessel v = FlightGlobals.ActiveVessel;
                if (v != null && v.packed) OrbitPhysicsManager.HoldVesselUnpack(2);
            }
        }

        // The TimingManager instance can change with the scene; re-register with the one alive now.
        private static void HookTiming()
        {
            object tm = typeof(TimingManager).GetProperty("Instance", BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null, null);
            if (tm == null || ReferenceEquals(tm, _timing)) return;
            _timing = tm;
            // (traces first: at BetterLateThanNever, before Late ends the frame)
            foreach (TimingManager.TimingStage stage in Enum.GetValues(typeof(TimingManager.TimingStage)))
            {
                string name = stage.ToString();
                TimingManager.FixedUpdateAdd(stage, () => Trace(name));
            }
            TimingManager.FixedUpdateAdd(TimingManager.TimingStage.ObscenelyEarly, Early);
            TimingManager.FixedUpdateAdd(TimingManager.TimingStage.BetterLateThanNever, Late);
        }

        /// <summary>Frames (from 0) to trace at every TimingManager stage: the vessel's and the world's state there.</summary>
        public static int TraceFrames = 0;
        private static StreamWriter _trace;

        private static void Trace(string stage)
        {
            if (State != Phase.Running || Frame >= TraceFrames || _v == null || _outPath == null) return;
            try
            {
                if (_trace == null) _trace = new StreamWriter(Path.ChangeExtension(_outPath, ".trace"), false, new UTF8Encoding(false));
                Rigidbody rb = _v.rootPart.Rigidbody;
                var fo = FloatingOrigin.fetch;
                _trace.WriteLine(string.Join(",", Frame.ToString(CultureInfo.InvariantCulture), stage, H(Planetarium.GetUniversalTime()),
                    "kv=" + H(Krakensbane.GetFrameVelocity()), "rbv=" + H(rb.velocity), "rbp=" + H(rb.position), "rbw=" + H(rb.angularVelocity),
                    "vrbv=" + H(_v.rb_velocityD), "vv=" + H(_v.velocityD), "com=" + H(_v.CoMD), "m=" + H(_v.totalMass),
                    "fo=" + H(fo.offset), "fonk=" + H(fo.offsetNonKrakensbane), "body=" + H(_v.mainBody.position),
                    "p=" + H(_v.ctrlState.pitch), "rho=" + H(_v.atmDensity)));
                if (stage == "BetterLateThanNever" && Frame == TraceFrames - 1) { _trace.Dispose(); _trace = null; }
            }
            catch (Exception ex) { Log.Warn("FlightReplay trace: " + ex.Message); }
        }

        private static void Fail(string why)
        {
            Error = why;
            State = Phase.Failed;
            Log.Warn("FlightReplay: " + why);
            Stop();
        }

        private static void Early()
        {
            try
            {
                Vessel v = FlightGlobals.ActiveVessel;
                switch (State)
                {
                    case Phase.Holding:
                        if (v != null && v.packed) OrbitPhysicsManager.HoldVesselUnpack(2);
                        return;
                    case Phase.Freezing:
                        FreezeFrame(v);
                        return;
                    case Phase.Starting:
                        StartFrame(v);
                        return;
                    case Phase.Running:
                        if (v != _v || _v.parts.Count != _parts.Length) { Fail($"frame {Frame}: the vessel changed ({(_v != null ? _v.parts.Count : 0)} parts, {_parts.Length} at the start)"); return; }
                        if (_record) _rec.Frames.Add(Capture(_parts));
                        else Apply(_parts, _rec.Frames[Frame]);
                        Restat(v);
                        Atmosphere(v);
                        PinScaledSun();
                        Trace("forced");
                        return;
                }
            }
            catch (Exception ex) { Fail("early: " + ex); }
        }

        private static void FreezeFrame(Vessel v)
        {
            if (v == null) return;
            if (!v.packed) { Fail("the vessel came off rails before the start"); return; }
            OrbitPhysicsManager.HoldVesselUnpack(2);
            if (!_frozen)
            {
                _v = v;
                if (_record)
                {
                    _parts = v.parts.ToArray();
                    _rec.Ids = _parts.Select(p => p.flightID).ToArray();
                    _rec.World = WorldState.Capture();
                    v.orbitDriver.updateFromParameters();
                    _rec.Poses = Capture(_parts);
                }
                else
                {
                    var byId = v.parts.ToDictionary(p => p.flightID);
                    if (_rec.Ids.Length != v.parts.Count || _rec.Ids.Any(id => !byId.ContainsKey(id)))
                    {
                        Fail($"the loaded vessel's parts ({v.parts.Count}) are not the recording's ({_rec.Ids.Length})");
                        return;
                    }
                    _parts = _rec.Ids.Select(id => byId[id]).ToArray();
                }
                _frozen = true;
                int restored = Pose(v, out string missing);
                if (missing.Length > 0) Log.Warn("FlightReplay: world fields not restored: " + missing);
                Log.Info($"FlightReplay: frozen at the start ({restored} world fields); voxelizing there");
                _freezeSections = CurrentSections(v);
                RebuildMeshData(v);
                RequestVoxels(v);
                return;
            }
            Pose(v, out _);
            if (!ReferenceEquals(CurrentSections(v), _freezeSections)) Voxelized = true;
        }

        // The start: the recorded world, the packed vessel on its orbit there, each part at its recorded pose.
        private static int Pose(Vessel v, out string missing)
        {
            int n = WorldState.Restore(_rec.World, out missing);
            v.orbitDriver.updateFromParameters();
            Apply(_parts, _rec.Poses, poseOnly: true);
            return n;
        }

        // FAR's GeometryPartModule keeps each mesh's matrices into the part's and the vessel's frame from when it
        // was built, at load, wherever the vessel was then; they feed the voxels' bounds and every part's data.
        // Rebuilt here, at the frozen pose.
        private static void RebuildMeshData(Vessel v)
        {
            int n = 0;
            foreach (Part p in v.parts)
                foreach (PartModule m in p.Modules)
                    if (m.GetType().Name == "GeometryPartModule")
                    {
                        MethodInfo rebuild = m.GetType().GetMethod("GeometryPartModuleRebuildMeshData", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                        if (rebuild == null) throw new UITestFailure("FAR's GeometryPartModule.GeometryPartModuleRebuildMeshData not found");
                        rebuild.Invoke(m, null);
                        n++;
                    }
            Log.Info($"FlightReplay: rebuilt FAR mesh data of {n} parts at the start pose");
        }

        private static void RequestVoxels(Vessel v)
        {
            foreach (VesselModule vm in v.GetComponents<VesselModule>())
                if (vm.GetType().Name == "FARVesselAero")
                {
                    MethodInfo m = vm.GetType().GetMethod("VesselUpdateEvent", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (m == null) throw new UITestFailure("FARVesselAero.VesselUpdateEvent not found");
                    m.Invoke(vm, new object[] { v });
                }
        }

        private static void StartFrame(Vessel v)
        {
            if (v == null) return;
            if (!v.packed) { Fail("the vessel came off rails before the start"); return; }
            Pose(v, out _);
            v.GoOffRails();
            WorldState.Restore(_rec.World, out _);
            if (_record) _rec.Frames.Add(Capture(_parts));
            else Apply(_parts, _rec.Frames[0]);
            Restat(v);
            Atmosphere(v);
            PinScaledSun();
            v.OnPostAutopilotUpdate += Inputs;
            _sections = CurrentSections(v);
            _out = new StreamWriter(_outPath, false, new UTF8Encoding(false));
            _state = _statePath != null ? new StreamWriter(_statePath, false, new UTF8Encoding(false)) : null;
            WriteHeader();
            Frame = 0;
            State = Phase.Running;
            Log.Info($"FlightReplay: {(_record ? "recording" : "replaying")} {_frames} frames of '{v.vesselName}' ({_parts.Length} parts) from UT {Planetarium.GetUniversalTime():R}");
        }

        // The vessel's mass, CoM and velocity (VesselPrecalculate) as the forced parts give them. KSP sums them before
        // this stage from the state physics left, and again in Update on render frames, which come with the wall
        // clock; Krakensbane moves the vessel's velocity into its frame velocity from them later in the frame.
        // Likewise where the vessel is and how it moves over the ground (latitude, longitude, altitude, surface
        // velocity and speed: Vessel.UpdatePosVel), which KSP updates once per render frame - so two physics frames
        // in one render frame leave the second with the first's; FAR reads them.
        private static void Restat(Vessel v)
        {
            if (v.precalc != null) v.precalc.CalculatePhysicsStats();
            v.UpdatePosVel();
        }

        // The air as FAR reads it, taken as the recording found it at this stage: KSP's FlightIntegrator derives the
        // temperature (and so density, Mach, dynamic pressure) with a sun direction taken from scaled space, which
        // moves on render frames, so it differs between runs in the 8th digit.
        private static void Atmosphere(Vessel v)
        {
            if (_record) _rec.Atm.Add(AtmCodec.Capture(v, _parts));
            else if (Frame < _rec.Atm.Count) AtmCodec.Apply(v, _parts, _rec.Atm[Frame]);
        }

        private static class AtmCodec
        {
            private static readonly string[] VesselNames = { "atmDensity", "atmosphericTemperature", "externalTemperature", "staticPressurekPa", "dynamicPressurekPa",
                                                             "mach", "speedOfSound" };
            private static readonly string[] PartNames = { "atmDensity", "dynamicPressurekPa", "staticPressureAtm", "machNumber", "submergedDynamicPressurekPa", "externalTemperature" };
            private static readonly FieldInfo[] VesselFields = Doubles(typeof(Vessel), VesselNames);
            private static readonly FieldInfo[] PartFields = Doubles(typeof(Part), PartNames);

            private static FieldInfo[] Doubles(Type t, string[] names) =>
                names.Select(n => t.GetField(n, BindingFlags.Instance | BindingFlags.Public)).Where(f => f != null && f.FieldType == typeof(double)).ToArray();

            // and each engine's throttle as it has spooled (FAR's intake ram drag scales with it; KSP moves it with
            // the intake air it computes from the same noisy air)
            private static IEnumerable<ModuleEngines> Engines(Part[] parts) => parts.SelectMany(p => p.Modules.OfType<ModuleEngines>());

            public static double[] Capture(Vessel v, Part[] parts)
            {
                var d = new List<double>();
                foreach (FieldInfo f in VesselFields) d.Add((double)f.GetValue(v));
                foreach (Part p in parts)
                    foreach (FieldInfo f in PartFields) d.Add((double)f.GetValue(p));
                foreach (ModuleEngines e in Engines(parts)) d.Add(e.currentThrottle);
                return d.ToArray();
            }

            public static void Apply(Vessel v, Part[] parts, double[] d)
            {
                int k = 0;
                foreach (FieldInfo f in VesselFields) f.SetValue(v, d[k++]);
                foreach (Part p in parts)
                    foreach (FieldInfo f in PartFields) f.SetValue(p, d[k++]);
                foreach (ModuleEngines e in Engines(parts))
                    if (k < d.Length) e.currentThrottle = (float)d[k++];
            }
        }

        // KSP's FlightIntegrator points at the sun from scaled space: the sun's scaled transform, placed on render
        // frames, against the vessel through ScaledSpace.totalOffset, which render frames accumulate. FAR's own
        // FlightIntegrator hooks then work from the air computed that way. Each frame: the offset as it stood at the
        // start (the recording's), and the sun's scaled position from where the sun is now.
        private static readonly FieldInfo ScaledOffset = typeof(ScaledSpace).GetField("totalOffset", BindingFlags.NonPublic | BindingFlags.Static);
        private static Vector3d? _scaledOffset;

        private static void PinScaledSun()
        {
            if (ScaledOffset == null) return;
            if (_scaledOffset == null)
            {
                string line = _rec.World.FirstOrDefault(l => l.StartsWith("ScaledSpace.totalOffset=", StringComparison.Ordinal));
                _scaledOffset = line != null ? (Vector3d)ValueCodec.Read(line.Substring(line.IndexOf('=') + 1), typeof(Vector3d))
                                             : (Vector3d)ScaledOffset.GetValue(null);
            }
            ScaledOffset.SetValue(null, _scaledOffset.Value);
            CelestialBody sun = Planetarium.fetch.Sun;
            if (sun != null && sun.scaledBody != null)
                sun.scaledBody.transform.position = ScaledSpace.LocalToScaledSpace(sun.position);
        }

        private static void Inputs(FlightCtrlState s)
        {
            if (State != Phase.Running) return;
            if (_record) Script?.Invoke(Frame, s);
            else if (Frame < _rec.Ctrl.Count) CtrlCodec.Read(_rec.Ctrl[Frame], s);
        }

        private static void Late()
        {
            if (State != Phase.Running) return;
            try
            {
                if (_record) _rec.Ctrl.Add(CtrlCodec.Write(_v.ctrlState));
                object sections = CurrentSections(_v);
                if (!ReferenceEquals(sections, _sections)) { VoxelSwaps.Add(Frame); _sections = sections; }
                WriteFrame(Frame);
                if (_state != null && (Frame % StateEvery == 0 || Frame == _frames - 1))
                {
                    _state.WriteLine("# frame " + Frame);
                    foreach (var kv in FarStateSnapshot.Take(_v).OrderBy(kv => kv.Key, StringComparer.Ordinal))
                        _state.WriteLine(kv.Key + "=" + kv.Value);
                }
                AfterFrame?.Invoke(Frame, _v);
                Frame++;
                if (Frame >= _frames)
                {
                    if (_record) _rec.Write(_recPath);
                    State = Phase.Done;
                    Stop();
                    State = Phase.Done;
                    Log.Info($"FlightReplay: done, {Frame} frames; voxel swaps at [{string.Join(" ", VoxelSwaps)}]");
                }
            }
            catch (Exception ex) { Fail("late: " + ex); }
        }

        private static object CurrentSections(Vessel v)
        {
            foreach (VesselModule vm in v.GetComponents<VesselModule>())
                if (vm.GetType().Name == "FARVesselAero")
                    return vm.GetType().GetField("_currentAeroSections", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(vm);
            return null;
        }

        // --- part states -------------------------------------------------------------------------------------------

        private const int PartFloats = 13;

        private static float[] Capture(Part[] parts)
        {
            var f = new float[parts.Length * PartFloats];
            for (int i = 0; i < parts.Length; ++i)
            {
                Rigidbody rb = parts[i].Rigidbody;
                int o = i * PartFloats;
                if (rb == null) { for (int k = 0; k < PartFloats; ++k) f[o + k] = float.NaN; continue; }
                Vector3 p = rb.position, v = rb.velocity, w = rb.angularVelocity;
                Quaternion q = rb.rotation;
                f[o] = p.x; f[o + 1] = p.y; f[o + 2] = p.z;
                f[o + 3] = q.x; f[o + 4] = q.y; f[o + 5] = q.z; f[o + 6] = q.w;
                f[o + 7] = v.x; f[o + 8] = v.y; f[o + 9] = v.z;
                f[o + 10] = w.x; f[o + 11] = w.y; f[o + 12] = w.z;
            }
            return f;
        }

        private static void Apply(Part[] parts, float[] f, bool poseOnly = false)
        {
            for (int i = 0; i < parts.Length; ++i)
            {
                Rigidbody rb = parts[i].Rigidbody;
                int o = i * PartFloats;
                if (rb == null || float.IsNaN(f[o])) continue;
                var p = new Vector3(f[o], f[o + 1], f[o + 2]);
                var q = new Quaternion(f[o + 3], f[o + 4], f[o + 5], f[o + 6]);
                Transform t = parts[i].transform;
                t.SetPositionAndRotation(p, q);
                rb.position = p;
                rb.rotation = q;
                if (poseOnly) continue;
                rb.velocity = new Vector3(f[o + 7], f[o + 8], f[o + 9]);
                rb.angularVelocity = new Vector3(f[o + 10], f[o + 11], f[o + 12]);
            }
        }

        // --- outputs ------------------------------------------------------------------------------------------------

        private static string H(float x) => BitConverter.ToInt32(BitConverter.GetBytes(x), 0).ToString("x8");
        private static string H(double x) => BitConverter.DoubleToInt64Bits(x).ToString("x16");
        private static string HObj(object o) => o is double d ? H(d) : o is float f ? H(f) : "";
        private static string H(Vector3 a) => H(a.x) + "," + H(a.y) + "," + H(a.z);
        private static string H(Vector3d a) => H(a.x) + "," + H(a.y) + "," + H(a.z);

        private static void WriteHeader()
        {
            _out.WriteLine("# FlightReplay " + (_record ? "record" : "replay") + " " + _recPath);
            _out.WriteLine("# F,frame,UT,atmDensity,mach,srfSpeed,altitude,latitude,longitude,externalTemperature,frameVel(3),bodyPos(3),CoM(3),rootPos(3),rootVel(3)");
            _out.WriteLine("# W,frame,part,worldSpaceForce(3),AerodynamicCenter(3),stall,deflection,shielded");
            _out.WriteLine("# A,frame,part,worldSpaceAeroForce(3),worldSpaceTorque(3)");
            for (int i = 0; i < _parts.Length; ++i)
                _out.WriteLine($"# part {i} {_parts[i].flightID} {_parts[i].partInfo.name}");
        }

        private static void WriteFrame(int frame)
        {
            Vessel v = _v;
            Rigidbody root = v.rootPart.Rigidbody;
            _out.WriteLine(string.Join(",", "F", frame.ToString(CultureInfo.InvariantCulture), H(Planetarium.GetUniversalTime()),
                H(v.atmDensity), H(v.mach), H(v.srfSpeed), H(v.altitude), H(v.latitude), H(v.longitude), H(v.externalTemperature),
                H(Krakensbane.GetFrameVelocity()), H(v.mainBody.position), H((Vector3d)v.CoM),
                H(root != null ? root.position : Vector3.zero), H(root != null ? root.velocity : Vector3.zero),
                Diagnostics(v)));
            for (int i = 0; i < _parts.Length; ++i)
                foreach (PartModule m in _parts[i].Modules)
                {
                    Type t = m.GetType();
                    if (IsA(t, "FARWingAerodynamicModel"))
                    {
                        var f = (Vector3)Field(m, "worldSpaceForce");
                        var ac = (Vector3d)Field(m, "AerodynamicCenter");
                        object stall = Field(m, "stall"), defl = Field(m, "AoAoffset"), sh = Field(m, "isShielded");
                        _out.WriteLine(string.Join(",", "W", frame.ToString(CultureInfo.InvariantCulture), i.ToString(CultureInfo.InvariantCulture),
                            H(f), H(ac), HObj(stall), HObj(defl), sh is bool b ? (b ? "1" : "0") : ""));
                    }
                    else if (t.Name == "FARAeroPartModule")
                    {
                        var f = (Vector3)Field(m, "worldSpaceAeroForce");
                        var tq = (Vector3)Field(m, "worldSpaceTorque");
                        _out.WriteLine(string.Join(",", "A", frame.ToString(CultureInfo.InvariantCulture), i.ToString(CultureInfo.InvariantCulture), H(f), H(tq)));
                    }
                }
        }

        // KSP's own intermediates that feed FAR's inputs, to find where runs part: the FlightIntegrator's sun
        // direction, its dot with up and the temperature offset from it; each engine's spooled throttle and each
        // intake's air flow.
        private static string Diagnostics(Vessel v)
        {
            var fi = v.GetComponent<FlightIntegrator>();
            var s = new List<string>();
            if (fi != null) { s.Add(H(fi.sunVector)); s.Add(H(fi.sunDot)); s.Add(H(fi.atmosphereTemperatureOffset)); }
            else s.Add(",,,,");
            foreach (Part p in _parts)
                foreach (PartModule m in p.Modules)
                {
                    if (m is ModuleEngines e) s.Add(H(e.currentThrottle));
                    else if (m is ModuleResourceIntake i) s.Add(H(i.airFlow));
                }
            return string.Join(",", s);
        }

        private static bool IsA(Type t, string name)
        {
            for (; t != null && t != typeof(PartModule); t = t.BaseType)
                if (t.Name == name) return true;
            return false;
        }

        private static readonly Dictionary<(Type, string), FieldInfo> FieldCache = new Dictionary<(Type, string), FieldInfo>();

        private static object Field(object o, string name)
        {
            Type t = o.GetType();
            if (!FieldCache.TryGetValue((t, name), out FieldInfo f))
            {
                for (Type b = t; b != null && f == null; b = b.BaseType)
                    f = b.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                FieldCache[(t, name)] = f;
            }
            return f?.GetValue(o);
        }

        // --- the recording file -------------------------------------------------------------------------------------

        private sealed class Recording
        {
            public uint[] Ids;
            public List<string> World;
            public float[] Poses;
            public readonly List<float[]> Frames = new List<float[]>();
            public readonly List<string> Ctrl = new List<string>();
            public readonly List<double[]> Atm = new List<double[]>();

            private const string Magic = "KspTest.FlightReplay/2";

            public void Write(string path)
            {
                using (var w = new BinaryWriter(File.Create(path)))
                {
                    w.Write(Magic);
                    w.Write(Ids.Length);
                    foreach (uint id in Ids) w.Write(id);
                    w.Write(World.Count);
                    foreach (string s in World) w.Write(s);
                    foreach (float x in Poses) w.Write(x);
                    w.Write(Frames.Count);
                    w.Write(Atm.Count > 0 ? Atm[0].Length : 0);
                    for (int i = 0; i < Frames.Count; ++i)
                    {
                        foreach (float x in Frames[i]) w.Write(x);
                        w.Write(Ctrl[i]);
                        foreach (double x in Atm[i]) w.Write(x);
                    }
                }
            }

            public static Recording Read(string path)
            {
                var r = new Recording();
                using (var br = new BinaryReader(File.OpenRead(path)))
                {
                    if (br.ReadString() != Magic) throw new UITestFailure("not a flight recording (" + Magic + "): " + path);
                    r.Ids = new uint[br.ReadInt32()];
                    for (int i = 0; i < r.Ids.Length; ++i) r.Ids[i] = br.ReadUInt32();
                    int nw = br.ReadInt32();
                    r.World = new List<string>(nw);
                    for (int i = 0; i < nw; ++i) r.World.Add(br.ReadString());
                    r.Poses = new float[r.Ids.Length * PartFloats];
                    for (int k = 0; k < r.Poses.Length; ++k) r.Poses[k] = br.ReadSingle();
                    int nf = br.ReadInt32(), na = br.ReadInt32();
                    for (int i = 0; i < nf; ++i)
                    {
                        var f = new float[r.Ids.Length * PartFloats];
                        for (int k = 0; k < f.Length; ++k) f[k] = br.ReadSingle();
                        r.Frames.Add(f);
                        r.Ctrl.Add(br.ReadString());
                        var a = new double[na];
                        for (int k = 0; k < na; ++k) a[k] = br.ReadDouble();
                        r.Atm.Add(a);
                    }
                }
                return r;
            }
        }

        private static class CtrlCodec
        {
            private static readonly FieldInfo[] Fields = typeof(FlightCtrlState).GetFields(BindingFlags.Instance | BindingFlags.Public)
                .Where(f => f.FieldType == typeof(float) || f.FieldType == typeof(bool)).OrderBy(f => f.Name, StringComparer.Ordinal).ToArray();

            public static string Write(FlightCtrlState s) => string.Join(";", Fields.Select(f => f.Name + "=" + ValueCodec.Write(f.GetValue(s))));

            public static void Read(string line, FlightCtrlState s)
            {
                foreach (string kv in line.Split(';'))
                {
                    int e = kv.IndexOf('=');
                    FieldInfo f = Fields.FirstOrDefault(x => x.Name == kv.Substring(0, e));
                    if (f != null) f.SetValue(s, ValueCodec.Read(kv.Substring(e + 1), f.FieldType));
                }
            }
        }
    }

    /// <summary>
    /// The world as KSP keeps it outside the vessel: every value field (numbers, flags, vectors and other structs of
    /// them, as their exact bits) of Planetarium (and its statics), Krakensbane, FloatingOrigin, each celestial body,
    /// its orbit driver and its transform's position and rotation. Lines "key.field=value".
    /// </summary>
    public static class WorldState
    {
        private const BindingFlags Inst = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        private const BindingFlags Stat = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        // particle bookkeeping (a count into a list this scene rebuilt) - not the world
        private static readonly HashSet<string> Skip = new HashSet<string> { "FloatingOrigin.pCount" };

        private static IEnumerable<(string key, object target, Type type, bool statics)> Targets()
        {
            yield return ("Planetarium", Planetarium.fetch, typeof(Planetarium), false);
            yield return ("Planetarium.static", null, typeof(Planetarium), true);
            yield return ("ScaledSpace", null, typeof(ScaledSpace), true);
            yield return ("Krakensbane", typeof(Krakensbane).GetField("fetch", BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null), typeof(Krakensbane), false);
            yield return ("FloatingOrigin", FloatingOrigin.fetch, typeof(FloatingOrigin), false);
            foreach (CelestialBody b in FlightGlobals.Bodies)
            {
                yield return ("Body." + b.bodyName, b, typeof(CelestialBody), false);
                if (b.orbitDriver != null)
                    yield return ("OrbitDriver." + b.bodyName, b.orbitDriver, typeof(OrbitDriver), false);
            }
        }

        private static IEnumerable<FieldInfo> FieldsOf(Type type, bool statics)
        {
            for (Type t = type; t != null && t != typeof(MonoBehaviour) && t != typeof(Behaviour) && t != typeof(Component) && t != typeof(object); t = t.BaseType)
            {
                foreach (FieldInfo f in t.GetFields(statics ? Stat : Inst))
                    if (!f.IsLiteral && !(statics && f.IsInitOnly) && ValueCodec.Supported(f.FieldType))
                        yield return f;
                if (statics) break;
            }
        }

        public static List<string> Capture()
        {
            var lines = new List<string>();
            foreach (var (key, target, type, statics) in Targets())
            {
                if (target == null && !statics) continue;
                foreach (FieldInfo f in FieldsOf(type, statics))
                {
                    if (Skip.Contains(key + "." + f.Name)) continue;
                    lines.Add(key + "." + f.Name + "=" + ValueCodec.Write(f.GetValue(target)));
                }
                if (target is CelestialBody b)
                {
                    lines.Add("Transform." + b.bodyName + ".position=" + ValueCodec.Write(b.transform.position));
                    lines.Add("Transform." + b.bodyName + ".rotation=" + ValueCodec.Write(b.transform.rotation));
                }
            }
            return lines;
        }

        public static int Restore(List<string> lines, out string missing)
        {
            var targets = new Dictionary<string, (object target, Type type, bool statics)>();
            foreach (var (key, target, type, statics) in Targets())
                targets[key] = (target, type, statics);
            var bodies = FlightGlobals.Bodies.ToDictionary(b => b.bodyName);
            int n = 0;
            var miss = new List<string>();
            foreach (string line in lines)
            {
                int e = line.IndexOf('=');
                string path = line.Substring(0, e), value = line.Substring(e + 1);
                if (path.StartsWith("Transform.", StringComparison.Ordinal))
                {
                    string rest = path.Substring("Transform.".Length);
                    int dot = rest.LastIndexOf('.');
                    if (!bodies.TryGetValue(rest.Substring(0, dot), out CelestialBody b)) { miss.Add(path); continue; }
                    if (rest.EndsWith(".position")) b.transform.position = (Vector3)ValueCodec.Read(value, typeof(Vector3));
                    else b.transform.rotation = (Quaternion)ValueCodec.Read(value, typeof(Quaternion));
                    n++;
                    continue;
                }
                int last = path.LastIndexOf('.');
                string key = path.Substring(0, last), field = path.Substring(last + 1);
                if (!targets.TryGetValue(key, out var tg) || (tg.target == null && !tg.statics)) { miss.Add(path); continue; }
                FieldInfo f = FieldsOf(tg.type, tg.statics).FirstOrDefault(x => x.Name == field);
                if (f == null) { miss.Add(path); continue; }
                f.SetValue(tg.target, ValueCodec.Read(value, f.FieldType));
                n++;
            }
            missing = string.Join(", ", miss.Take(20)) + (miss.Count > 20 ? $" ... ({miss.Count})" : "");
            return n;
        }
    }

    /// <summary>Exact text form of numbers, flags, enums and structs of them: floats and doubles as their bits.</summary>
    public static class ValueCodec
    {
        private const BindingFlags Inst = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private static readonly Dictionary<Type, bool> SupportedCache = new Dictionary<Type, bool>();

        public static bool Supported(Type t)
        {
            if (SupportedCache.TryGetValue(t, out bool ok)) return ok;
            SupportedCache[t] = false;   // (recursive structs)
            if (t == typeof(double) || t == typeof(float) || t == typeof(bool) || t.IsEnum || t == typeof(int) || t == typeof(uint)
                || t == typeof(long) || t == typeof(ulong) || t == typeof(short) || t == typeof(ushort) || t == typeof(byte) || t == typeof(sbyte))
                ok = true;
            else if (t.IsValueType && !t.IsPrimitive)
                ok = StructFields(t).Length > 0 && StructFields(t).All(f => Supported(f.FieldType));
            SupportedCache[t] = ok;
            return ok;
        }

        private static FieldInfo[] StructFields(Type t) =>
            t.GetFields(Inst).Where(f => !f.IsStatic).OrderBy(f => f.MetadataToken).ToArray();

        public static string Write(object v)
        {
            switch (v)
            {
                case double d: return "d" + BitConverter.DoubleToInt64Bits(d).ToString("x16");
                case float f: return "f" + BitConverter.ToInt32(BitConverter.GetBytes(f), 0).ToString("x8");
                case bool b: return b ? "T" : "F";
            }
            Type t = v.GetType();
            if (t.IsEnum || t.IsPrimitive) return "i" + Convert.ToInt64(v, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture);
            return "{" + string.Join("|", StructFields(t).Select(f => Write(f.GetValue(v)))) + "}";
        }

        public static object Read(string s, Type t)
        {
            int i = 0;
            object v = Read(s, ref i, t);
            return v;
        }

        private static object Read(string s, ref int i, Type t)
        {
            char c = s[i];
            if (c == '{')
            {
                object o = Activator.CreateInstance(t);
                i++;
                FieldInfo[] fs = StructFields(t);
                for (int k = 0; k < fs.Length; ++k)
                {
                    object fv = Read(s, ref i, fs[k].FieldType);
                    fs[k].SetValue(o, fv);   // boxed struct: set in place
                    if (s[i] == '|' || s[i] == '}') i++;
                }
                return o;
            }
            int end = i + 1;
            while (end < s.Length && s[end] != '|' && s[end] != '}') end++;
            string tok = s.Substring(i + 1, end - i - 1);
            i = end;
            switch (c)
            {
                case 'd': return BitConverter.Int64BitsToDouble(long.Parse(tok, NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                case 'f': return BitConverter.ToSingle(BitConverter.GetBytes(int.Parse(tok, NumberStyles.HexNumber, CultureInfo.InvariantCulture)), 0);
                case 'T': return true;
                case 'F': return false;
                case 'i':
                    long n = long.Parse(tok, CultureInfo.InvariantCulture);
                    return t.IsEnum ? Enum.ToObject(t, n) : Convert.ChangeType(n, t, CultureInfo.InvariantCulture);
            }
            throw new FormatException("bad value '" + s + "' at " + i);
        }
    }
}
