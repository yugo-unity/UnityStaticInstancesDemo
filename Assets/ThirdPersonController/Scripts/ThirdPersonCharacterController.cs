using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;

/// <summary>
/// Third-person character + camera-target controller.
/// Moves the CharacterController relative to the camera yaw, keeps it on the ground,
/// drives the StarterAssetsThirdPerson animator, and rotates the camera target that a
/// CinemachineThirdPersonFollow camera follows.
/// </summary>
[RequireComponent(typeof(CharacterController))]
[DefaultExecutionOrder(-100)] // camera target must be rotated before CinemachineBrain's LateUpdate
public class ThirdPersonCharacterController : MonoBehaviour
{
    [Header("Input")]
    [SerializeField] private InputActionAsset inputActions;
    [SerializeField] private string actionMapName = "Player";

    [Header("Movement")]
    [SerializeField] private float moveSpeed = 2.0f;
    [SerializeField] private float sprintSpeed = 5.335f;
    [SerializeField, Range(0.1f, 0.9f)] private float stickRunThreshold = 0.5f; // stick deflection: below = walk, above = blend to run
    [SerializeField] private float speedChangeRate = 10.0f;
    [SerializeField, Range(0.0f, 0.3f)] private float rotationSmoothTime = 0.12f;

    [Header("Ground")]
    [SerializeField] private LayerMask groundLayers = 1; // Default
    [SerializeField] private float groundedOffset = -0.14f;
    [SerializeField] private float groundedRadius = 0.28f;
    [SerializeField] private float gravity = -15.0f;
    [SerializeField] private float groundSnapDistance = 0.5f;
    [SerializeField] private float fallTimeout = 0.15f;

    [Header("Camera")]
    [SerializeField] private Transform cameraTarget;
    [SerializeField] private Transform mainCamera;
    [SerializeField] private float stickLookSpeed = 180.0f;   // deg/sec at full deflection
    [SerializeField] private float pointerLookSpeed = 0.15f;  // deg/pixel
    [SerializeField] private bool invertY = false;
    [SerializeField] private float topClamp = 70.0f;
    [SerializeField] private float bottomClamp = -30.0f;

    private CharacterController controller;
    private Animator animator;

    private InputAction moveAction;
    private InputAction lookAction;
    private InputAction pointerDeltaAction;
    private InputAction lookDragAction;
    private InputAction sprintAction;
    private bool pointerDragBlocked;

    private float targetYaw;
    private float targetPitch;

    private float speed;
    private float animationBlend;
    private float motionSpeed = 1.0f;
    private float targetRotation;
    private float rotationVelocity;
    private float verticalVelocity;
    private float fallTimeoutDelta;
    private bool grounded = true;

    private static readonly int SpeedId = Animator.StringToHash("Speed");
    private static readonly int GroundedId = Animator.StringToHash("Grounded");
    private static readonly int JumpId = Animator.StringToHash("Jump");
    private static readonly int FreeFallId = Animator.StringToHash("FreeFall");
    private static readonly int MotionSpeedId = Animator.StringToHash("MotionSpeed");

    private const float TerminalVelocity = -53.0f;

    private void Awake()
    {
        this.controller = this.GetComponent<CharacterController>();
        this.animator = this.GetComponent<Animator>();
        if (this.mainCamera == null && Camera.main != null)
            this.mainCamera = Camera.main.transform;

        if (this.inputActions != null)
        {
            var map = this.inputActions.FindActionMap(this.actionMapName, true);
            this.moveAction = map.FindAction("Move", true);
            this.lookAction = map.FindAction("Look", true);
            this.pointerDeltaAction = map.FindAction("PointerDelta", true);
            this.lookDragAction = map.FindAction("LookDrag", true);
            this.sprintAction = map.FindAction("Sprint", true);
        }
        else
        {
            Debug.LogError($"{nameof(ThirdPersonCharacterController)}: inputActions is not assigned.", this);
        }
    }

    private void OnEnable()
    {
        this.inputActions?.FindActionMap(this.actionMapName)?.Enable();
    }

    private void OnDisable()
    {
        this.inputActions?.FindActionMap(this.actionMapName)?.Disable();
    }

    private void Start()
    {
        var targetEuler = this.cameraTarget != null ? this.cameraTarget.rotation.eulerAngles : this.transform.eulerAngles;
        this.targetYaw = targetEuler.y;
        this.targetPitch = ClampAngle(targetEuler.x, this.bottomClamp, this.topClamp);
        this.targetRotation = this.transform.eulerAngles.y;
        this.fallTimeoutDelta = this.fallTimeout;

        if (this.animator != null)
            this.animator.applyRootMotion = false;
    }

    private void Update()
    {
        this.GroundedCheck();
        this.ApplyGravity();
        this.Move();
    }

    private void LateUpdate()
    {
        this.CameraRotation();
    }

    private void GroundedCheck()
    {
        var p = this.transform.position;
        var spherePosition = new Vector3(p.x, p.y - this.groundedOffset, p.z);
        this.grounded = Physics.CheckSphere(spherePosition, this.groundedRadius, this.groundLayers, QueryTriggerInteraction.Ignore);

        if (this.animator != null)
            this.animator.SetBool(GroundedId, this.grounded);
    }

    private void ApplyGravity()
    {
        if (this.grounded)
        {
            this.fallTimeoutDelta = this.fallTimeout;
            if (this.animator != null)
            {
                this.animator.SetBool(JumpId, false);
                this.animator.SetBool(FreeFallId, false);
            }

            // keep a small downward velocity so the controller stays in contact with the ground
            if (this.verticalVelocity < 0.0f)
                this.verticalVelocity = -2.0f;
        }
        else
        {
            if (this.fallTimeoutDelta >= 0.0f)
                this.fallTimeoutDelta -= Time.deltaTime;
            else if (this.animator != null)
                this.animator.SetBool(FreeFallId, true);
        }

        if (this.verticalVelocity > TerminalVelocity)
            this.verticalVelocity += this.gravity * Time.deltaTime;
    }

    private void Move()
    {
        var input = this.moveAction != null ? this.moveAction.ReadValue<Vector2>() : Vector2.zero;
        var sprint = this.sprintAction != null && this.sprintAction.IsPressed();
        var inputMagnitude = Mathf.Clamp01(input.magnitude);
        var isKeyboard = this.moveAction?.activeControl?.device is Keyboard;

        float targetSpeed;
        if (inputMagnitude < 0.01f)
            targetSpeed = 0.0f;
        else if (sprint)
            targetSpeed = this.sprintSpeed;
        else if (isKeyboard)
            targetSpeed = this.moveSpeed;
        else if (inputMagnitude <= this.stickRunThreshold)
            targetSpeed = this.moveSpeed * (inputMagnitude / this.stickRunThreshold);
        else
            targetSpeed = Mathf.Lerp(this.moveSpeed, this.sprintSpeed, (inputMagnitude - this.stickRunThreshold) / (1.0f - this.stickRunThreshold));

        var currentHorizontalSpeed = new Vector3(this.controller.velocity.x, 0.0f, this.controller.velocity.z).magnitude;
        const float speedOffset = 0.1f;

        if (currentHorizontalSpeed < targetSpeed - speedOffset || currentHorizontalSpeed > targetSpeed + speedOffset)
        {
            this.speed = Mathf.Lerp(currentHorizontalSpeed, targetSpeed, Time.deltaTime * this.speedChangeRate);
            this.speed = Mathf.Round(this.speed * 1000.0f) / 1000.0f;
        }
        else
        {
            this.speed = targetSpeed;
        }

        // Below walk speed keep the walk cycle and slow its playback instead of blending toward idle (avoids foot sliding).
        var animTargetSpeed = targetSpeed > 0.0f ? Mathf.Max(targetSpeed, this.moveSpeed) : 0.0f;
        var animTargetMotionSpeed = targetSpeed > 0.0f ? Mathf.Clamp(targetSpeed / this.moveSpeed, 0.25f, 1.0f) : 1.0f;
        this.animationBlend = Mathf.Lerp(this.animationBlend, animTargetSpeed, Time.deltaTime * this.speedChangeRate);
        if (this.animationBlend < 0.01f)
            this.animationBlend = 0.0f;
        this.motionSpeed = Mathf.Lerp(this.motionSpeed, animTargetMotionSpeed, Time.deltaTime * this.speedChangeRate);

        if (input != Vector2.zero)
        {
            var cameraYaw = this.mainCamera != null ? this.mainCamera.eulerAngles.y : this.targetYaw;
            this.targetRotation = Mathf.Atan2(input.x, input.y) * Mathf.Rad2Deg + cameraYaw;
            var rotation = Mathf.SmoothDampAngle(this.transform.eulerAngles.y, this.targetRotation, ref this.rotationVelocity, this.rotationSmoothTime);
            this.transform.rotation = Quaternion.Euler(0.0f, rotation, 0.0f);
        }

        var targetDirection = Quaternion.Euler(0.0f, this.targetRotation, 0.0f) * Vector3.forward;
        var wasGrounded = this.controller.isGrounded;
        this.controller.Move(targetDirection.normalized * (this.speed * Time.deltaTime)
                             + new Vector3(0.0f, this.verticalVelocity, 0.0f) * Time.deltaTime);

        // snap down slopes and steps instead of briefly going airborne
        if (wasGrounded && !this.controller.isGrounded && this.verticalVelocity <= 0.0f)
            this.SnapToGround();

        if (this.animator != null)
        {
            this.animator.SetFloat(SpeedId, this.animationBlend);
            this.animator.SetFloat(MotionSpeedId, this.motionSpeed);
        }
    }

    private void SnapToGround()
    {
        var radius = this.controller.radius;
        var origin = this.transform.position + this.controller.center;
        var castDistance = this.controller.height * 0.5f - radius + this.groundSnapDistance;
        if (Physics.SphereCast(origin, radius, Vector3.down, out var hit, castDistance, this.groundLayers, QueryTriggerInteraction.Ignore)
            && Vector3.Angle(hit.normal, Vector3.up) <= this.controller.slopeLimit)
        {
            var drop = hit.distance - (this.controller.height * 0.5f - radius) + this.controller.skinWidth;
            if (drop > 0.0f)
                this.controller.Move(Vector3.down * drop);
        }
    }

    private void CameraRotation()
    {
        var delta = Vector2.zero;

        if (this.lookAction != null)
            delta += this.lookAction.ReadValue<Vector2>() * (this.stickLookSpeed * Time.deltaTime);

        if (this.lookDragAction != null)
        {
            // a drag that starts on UI is left to the UI
            if (this.lookDragAction.WasPressedThisFrame())
                this.pointerDragBlocked = EventSystem.current != null && EventSystem.current.IsPointerOverGameObject();

            if (this.lookDragAction.IsPressed() && !this.pointerDragBlocked)
                delta += this.pointerDeltaAction.ReadValue<Vector2>() * this.pointerLookSpeed;
        }

        this.targetYaw += delta.x;
        this.targetPitch += this.invertY ? delta.y : -delta.y;

        this.targetYaw = ClampAngle(this.targetYaw, float.MinValue, float.MaxValue);
        this.targetPitch = ClampAngle(this.targetPitch, this.bottomClamp, this.topClamp);

        if (this.cameraTarget != null)
            this.cameraTarget.rotation = Quaternion.Euler(this.targetPitch, this.targetYaw, 0.0f);
    }

    private static float ClampAngle(float angle, float min, float max)
    {
        if (angle < -360.0f) angle += 360.0f;
        if (angle > 360.0f) angle -= 360.0f;
        if (angle > 180.0f && max <= 180.0f) angle -= 360.0f;
        return Mathf.Clamp(angle, min, max);
    }

    // Animation event receivers for the StarterAssets clips (footstep / landing).
    private void OnFootstep(AnimationEvent animationEvent) { }
    private void OnLand(AnimationEvent animationEvent) { }

    private void OnDrawGizmosSelected()
    {
        Gizmos.color = this.grounded ? new Color(0.0f, 1.0f, 0.0f, 0.35f) : new Color(1.0f, 0.0f, 0.0f, 0.35f);
        var p = this.transform.position;
        Gizmos.DrawSphere(new Vector3(p.x, p.y - this.groundedOffset, p.z), this.groundedRadius);
    }
}
