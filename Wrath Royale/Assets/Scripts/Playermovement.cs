using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// First person player movement til Unity 6 (new Input System).
/// Gå, løb, hop, crouch og slide (løb + crouch) + mus-styret kamera.
///
/// Opsætning:
/// 1. Opret et tomt GameObject (fx "Player") og sæt dette script på det.
///    CharacterController tilføjes automatisk. Pivot skal ligge ved fødderne.
/// 2. Opret et tomt child-objekt "CameraPivot" og træk det ind i feltet Camera Pivot.
/// 3. Gør Main Camera til child af CameraPivot (position 0,0,0).
/// </summary>
[RequireComponent(typeof(CharacterController))]
public class PlayerMovement : MonoBehaviour
{
    [Header("Kamera")]
    [Tooltip("Tomt objekt som kameraet er child af. Roteres op/ned af musen.")]
    [SerializeField] private Transform cameraPivot;
    [SerializeField] private float mouseSensitivity = 0.1f;
    [SerializeField] private float maxLookAngle = 85f;
    [SerializeField] private bool lockCursor = true;

    [Header("Bevægelse")]
    [SerializeField] private float walkSpeed = 4f;
    [SerializeField] private float runSpeed = 7f;
    [SerializeField] private float crouchSpeed = 2f;
    [SerializeField] private float groundAcceleration = 40f;
    [SerializeField] private float airAcceleration = 8f;

    [Header("Hop & tyngdekraft")]
    [SerializeField] private float jumpHeight = 1.2f;
    [SerializeField] private float gravity = 25f;

    [Header("Crouch")]
    [SerializeField] private float standingHeight = 1.8f;
    [SerializeField] private float crouchHeight = 1.0f;
    [SerializeField] private float standingEyeHeight = 1.6f;
    [SerializeField] private float crouchEyeHeight = 0.8f;
    [SerializeField] private float crouchTransitionSpeed = 10f;
    [Tooltip("Hvilke lag der blokerer for at rejse sig (loft m.m.).")]
    [SerializeField] private LayerMask ceilingMask = ~0;

    [Header("Slide")]
    [SerializeField] private float slideStartSpeed = 10f;
    [SerializeField] private float slideEndSpeed = 3f;
    [SerializeField] private float slideDuration = 0.9f;
    [SerializeField] private float slideCooldown = 0.5f;

    // Tilstand (kan læses fra andre scripts)
    public bool IsCrouching { get; private set; }
    public bool IsSliding { get; private set; }
    public bool IsGrounded => controller.isGrounded;

    private CharacterController controller;
    private Vector3 horizontalVelocity;
    private float verticalVelocity;
    private float pitch;

    private Vector3 slideDirection;
    private float slideTimer;
    private float nextSlideTime;

    private void Awake()
    {
        controller = GetComponent<CharacterController>();
        controller.height = standingHeight;
        controller.center = Vector3.up * (standingHeight * 0.5f);

        if (cameraPivot != null)
            cameraPivot.localPosition = new Vector3(0f, standingEyeHeight, 0f);
    }

    private void OnEnable()
    {
        if (lockCursor)
            SetCursorLocked(true);
    }

    private void Update()
    {
        Keyboard kb = Keyboard.current;
        Mouse mouse = Mouse.current;
        if (kb == null || mouse == null) return;

        // Escape frigiver musen, klik fanger den igen
        if (lockCursor)
        {
            if (kb.escapeKey.wasPressedThisFrame) SetCursorLocked(false);
            else if (mouse.leftButton.wasPressedThisFrame && Cursor.lockState != CursorLockMode.Locked)
                SetCursorLocked(true);
        }

        if (Cursor.lockState == CursorLockMode.Locked)
            HandleLook(mouse);

        // ---------- Input ----------
        Vector2 moveInput = new Vector2(
            (kb.dKey.isPressed ? 1f : 0f) - (kb.aKey.isPressed ? 1f : 0f),
            (kb.wKey.isPressed ? 1f : 0f) - (kb.sKey.isPressed ? 1f : 0f));
        moveInput = Vector2.ClampMagnitude(moveInput, 1f);

        bool sprintHeld = kb.leftShiftKey.isPressed;
        bool crouchHeld = kb.leftCtrlKey.isPressed || kb.cKey.isPressed;
        bool crouchPressed = kb.leftCtrlKey.wasPressedThisFrame || kb.cKey.wasPressedThisFrame;
        bool jumpPressed = kb.spaceKey.wasPressedThisFrame;

        bool grounded = controller.isGrounded;
        float dt = Time.deltaTime;

        // ---------- Start slide: løb + crouch ----------
        bool wantsSlide = crouchPressed || (crouchHeld && sprintHeld && !IsCrouching);
        if (!IsSliding && grounded && wantsSlide && sprintHeld
            && moveInput.sqrMagnitude > 0.1f && Time.time >= nextSlideTime)
        {
            StartSlide(moveInput);
        }

        // ---------- Crouch-tilstand ----------
        if (IsSliding)
        {
            IsCrouching = true;
        }
        else if (crouchHeld)
        {
            IsCrouching = true;
        }
        else if (IsCrouching && CanStand())
        {
            IsCrouching = false;
        }

        // ---------- Hop ----------
        if (jumpPressed && grounded)
        {
            if (IsSliding) EndSlide();
            verticalVelocity = Mathf.Sqrt(2f * jumpHeight * gravity);
        }

        // ---------- Horisontal bevægelse ----------
        if (IsSliding)
        {
            UpdateSlide(crouchHeld, dt);
        }
        else
        {
            float targetSpeed = IsCrouching ? crouchSpeed : (sprintHeld ? runSpeed : walkSpeed);
            Vector3 targetVelocity = transform.TransformDirection(new Vector3(moveInput.x, 0f, moveInput.y)) * targetSpeed;
            float accel = grounded ? groundAcceleration : airAcceleration;
            horizontalVelocity = Vector3.MoveTowards(horizontalVelocity, targetVelocity, accel * dt);
        }

        // ---------- Tyngdekraft ----------
        if (grounded && verticalVelocity < 0f)
            verticalVelocity = -2f;
        verticalVelocity -= gravity * dt;

        controller.Move((horizontalVelocity + Vector3.up * verticalVelocity) * dt);

        UpdateHeight(dt);
    }

    // ---------------- Kamera ----------------

    private void HandleLook(Mouse mouse)
    {
        Vector2 delta = mouse.delta.ReadValue() * mouseSensitivity;

        // Venstre/højre drejer hele spilleren
        transform.Rotate(Vector3.up, delta.x);

        // Op/ned drejer kun kameraet
        if (cameraPivot != null)
        {
            pitch = Mathf.Clamp(pitch - delta.y, -maxLookAngle, maxLookAngle);
            cameraPivot.localRotation = Quaternion.Euler(pitch, 0f, 0f);
        }
    }

    private static void SetCursorLocked(bool locked)
    {
        Cursor.lockState = locked ? CursorLockMode.Locked : CursorLockMode.None;
        Cursor.visible = !locked;
    }

    // ---------------- Slide ----------------

    private void StartSlide(Vector2 moveInput)
    {
        IsSliding = true;
        slideTimer = 0f;
        slideDirection = transform.TransformDirection(new Vector3(moveInput.x, 0f, moveInput.y)).normalized;
        horizontalVelocity = slideDirection * slideStartSpeed;
    }

    private void UpdateSlide(bool crouchHeld, float dt)
    {
        slideTimer += dt;
        float t = Mathf.Clamp01(slideTimer / slideDuration);

        // Hurtig start som aftager mod slutningen
        float speed = Mathf.Lerp(slideStartSpeed, slideEndSpeed, t * t);
        horizontalVelocity = slideDirection * speed;

        // Slut når tiden er gået, crouch slippes, eller vi forlader jorden
        if (t >= 1f || !crouchHeld || !controller.isGrounded)
            EndSlide();
    }

    private void EndSlide()
    {
        IsSliding = false;
        nextSlideTime = Time.time + slideCooldown;
    }

    // ---------------- Højde ----------------

    private void UpdateHeight(float dt)
    {
        float targetHeight = IsCrouching ? crouchHeight : standingHeight;
        float newHeight = Mathf.Lerp(controller.height, targetHeight, crouchTransitionSpeed * dt);
        if (Mathf.Abs(newHeight - targetHeight) < 0.001f) newHeight = targetHeight;

        controller.height = newHeight;
        controller.center = Vector3.up * (newHeight * 0.5f);

        if (cameraPivot != null)
        {
            float targetEye = IsCrouching ? crouchEyeHeight : standingEyeHeight;
            Vector3 p = cameraPivot.localPosition;
            p.y = Mathf.Lerp(p.y, targetEye, crouchTransitionSpeed * dt);
            cameraPivot.localPosition = p;
        }
    }

    /// <summary>Tjekker om der er plads over hovedet til at rejse sig.</summary>
    private bool CanStand()
    {
        float extra = standingHeight - controller.height;
        if (extra <= 0.01f) return true;

        float radius = controller.radius;
        Vector3 origin = transform.position + Vector3.up * (controller.height - radius);
        return !Physics.SphereCast(origin, radius * 0.95f, Vector3.up, out _, extra,
            ceilingMask, QueryTriggerInteraction.Ignore);
    }
}