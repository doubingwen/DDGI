using UnityEngine;
using UnityEngine.Scripting.APIUpdating;

namespace Dou.GI
{
    //标记捕获相机，probe相机会带这个标记
    [DisallowMultipleComponent]
    [MovedFrom(true, null, null, "ProbeCaptureCameraTag")]
    public sealed class RadianceCaptureCamera : MonoBehaviour
    {
    }
}
