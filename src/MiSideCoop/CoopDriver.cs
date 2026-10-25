using System;
using UnityEngine;

namespace MiSideCoop;

// The only type injected into IL2CPP: it forwards Unity's frame callbacks to managed code.
public sealed class CoopDriver : MonoBehaviour
{
    public CoopDriver(IntPtr ptr) : base(ptr) { }

    private void Update() => CoopRuntime.Update(this);
    private void LateUpdate() => CoopRuntime.LateUpdate();
    private void OnGUI() => CoopRuntime.OnGUI();
    private void OnApplicationQuit() => CoopRuntime.Shutdown();
}
