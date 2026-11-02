using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace KspTest
{
    /// <summary>
    /// The editor's cameras as they were (parent, local pose, field of view, clip planes, enabled) and its camera
    /// controllers' enabled state: taken before a shot poses them, put back after, so the player's view comes back.
    /// </summary>
    public sealed class CameraState
    {
        private struct Cam
        {
            public Camera C;
            public Transform Parent;
            public Vector3 LocalPos;
            public Quaternion LocalRot;
            public float Fov, Near, Far;
            public bool Enabled;
        }

        private readonly List<Cam> cams = new List<Cam>();
        private readonly List<(MonoBehaviour mb, bool enabled)> controllers = new List<(MonoBehaviour, bool)>();

        public static CameraState Take()
        {
            var s = new CameraState();
            foreach (var c in Camera.allCameras.Concat(Object.FindObjectsOfType<Camera>()).Distinct())
                if (c != null)
                    s.cams.Add(new Cam
                    {
                        C = c, Parent = c.transform.parent, LocalPos = c.transform.localPosition, LocalRot = c.transform.localRotation,
                        Fov = c.fieldOfView, Near = c.nearClipPlane, Far = c.farClipPlane, Enabled = c.enabled,
                    });
            foreach (var mb in Object.FindObjectsOfType<MonoBehaviour>())
                if (mb != null && (mb.GetType().Name == "SPHCamera" || mb.GetType().Name == "VABCamera"))
                    s.controllers.Add((mb, mb.enabled));
            return s;
        }

        public void Restore()
        {
            foreach (var k in cams)
            {
                if (k.C == null) continue;
                if (k.C.transform.parent != k.Parent) k.C.transform.SetParent(k.Parent, false);
                k.C.transform.localPosition = k.LocalPos;
                k.C.transform.localRotation = k.LocalRot;
                k.C.fieldOfView = k.Fov;
                k.C.nearClipPlane = k.Near;
                k.C.farClipPlane = k.Far;
                k.C.enabled = k.Enabled;
            }
            foreach (var (mb, enabled) in controllers)
                if (mb != null) mb.enabled = enabled;
        }
    }
}
