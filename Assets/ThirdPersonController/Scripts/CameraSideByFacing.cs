using Unity.Cinemachine;
using UnityEngine;

/// <summary>
/// Drives CinemachineThirdPersonFollow.CameraSide from the character's facing relative to the camera yaw.
/// Facing angle: 0 = away from the camera, +90 = screen right, -90 = screen left, +/-180 = toward the camera.
/// </summary>
[RequireComponent(typeof(CinemachineThirdPersonFollow))]
[DefaultExecutionOrder(-90)] // after ThirdPersonCharacterController (-100) rotates the camera target, before CinemachineBrain
public class CameraSideByFacing : MonoBehaviour
{
    [SerializeField] private Transform character;
    [Tooltip("Camera yaw reference. Defaults to the CinemachineCamera's Follow target.")]
    [SerializeField] private Transform cameraYawSource;
    [Tooltip("X: facing angle relative to the camera (deg, -180..180), Y: CameraSide")]
    [SerializeField] private AnimationCurve sideByAngle = new AnimationCurve(
        new Keyframe(-180.0f, 0.75f),
        new Keyframe(-90.0f, 0.5f),
        new Keyframe(0.0f, 1.0f),
        new Keyframe(90.0f, 1.0f),
        new Keyframe(180.0f, 0.75f));
    [SerializeField, Min(0.0f)] private float smoothTime = 0.5f;

    private CinemachineThirdPersonFollow follow;
    private float sideVelocity;

    private void Awake()
    {
        this.follow = this.GetComponent<CinemachineThirdPersonFollow>();
        if (this.cameraYawSource == null)
            this.cameraYawSource = this.GetComponent<CinemachineCamera>()?.Follow;
    }

    private void LateUpdate()
    {
        if (this.character == null || this.cameraYawSource == null)
            return;

        var angle = Mathf.DeltaAngle(this.cameraYawSource.eulerAngles.y, this.character.eulerAngles.y);
        var targetSide = Mathf.Clamp01(this.sideByAngle.Evaluate(angle));
        this.follow.CameraSide = Mathf.SmoothDamp(this.follow.CameraSide, targetSide, ref this.sideVelocity, this.smoothTime);
    }
}
