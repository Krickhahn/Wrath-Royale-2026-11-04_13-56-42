using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Netværksversion af PlayerMovement (Netcode for GameObjects 2.x, Unity 6).
/// Prototype: ejeren styrer selv sin bevægelse, og NetworkTransform synkroniserer positionen.
///
/// Player-prefab skal have:
///  - NetworkObject
///  - NetworkTransform (Authority Mode = Owner)
///  - CharacterController
///  - Dette script
///  - Et child "CameraPivot" med et Camera-child (træk begge ind i felterne nedenfor)
/// </summary>
[RequireComponent(typeof(CharacterController))]
public class NetworkPlayerMovement : NetworkBehaviour
{
    [Header("Kamera")]
    [SerializeField] private Transform cameraPivot;
    [SerializeField] private Camera playerCamera;
    [SerializeField] private AudioListener audioListener;
    [SerializeField] private float mouseSensitivity = 0.1f;
    [SerializeField] private float maxLookAngle = 85f;

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
    [SerializeField] private LayerMask ceilingMask = ~0;

    [Header("Slide")]
    [SerializeField] private float slideStartSpeed = 10f;
    [SerializeField] private float slideEndSpeed = 3f;
    [SerializeField] private float slideDuration = 0.9f;
    [SerializeField] private float slideCooldown = 0.5f;

    // Synkroniseres til alle klienter (kun ejeren må skrive)
    private readonly NetworkVariable<bool> netCrouching = new(false,
        NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);
    private readonly NetworkVariable<bool> netSliding = new(false,
        NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);

    public bool IsCrouching { get; private set; }
    public bool IsSliding { get; private set; }

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

    public override void OnNetworkSpawn()
    {
        if (IsOwner)
        {
            if (playerCamera != null) playerCamera.enabled = true;
            if (audioListener != null) audioListener.enabled = true;
            SetCursorLocked(true);
        }
        else
        {
            // Andre spilleres kamera må ikke være aktivt hos os
            if (playerCamera != null) playerCamera.enabled = false;
            if (audioListener != null) audioListener.enabled = false;

            // NetworkTransform flytter transformen direkte, og en aktiv
            // CharacterController kan modarbejde det. Derfor slås den fra på fremmede spillere.
            // (Hitbox til angreb er en separat trigger-collider, se PlayerHealth.)
            controller.enabled = false;
        }
    }

    public override void OnNetworkDespawn()
    {
        if (IsOwner) SetCursorLocked(false);
    }

    /// <summary>Nulstiller fart og slide. Kaldes ved respawn.</summary>
    public void ResetMotion()
    {
        horizontalVelocity = Vector3.zero;
        verticalVelocity = 0f;
        if (IsSliding) EndSlide();
    }

    private void Update()
    {
        if (!IsSpawned) return;

        // Fremmede spillere: vis bare crouch-højde ud fra de synkroniserede værdier
        if (!IsOwner)
        {
            IsCrouching = netCrouching.Value;
            IsSliding = netSliding.Value;
            UpdateHeight(Time.deltaTime);
            return;
        }

        Keyboard kb = Keyboard.current;
        Mouse mouse = Mouse.current;
        if (kb == null || mouse == null) return;

        if (kb.escapeKey.wasPressedThisFrame) SetCursorLocked(false);
        else if (mouse.leftButton.wasPressedThisFrame && Cursor.lockState != CursorLockMode.Locked)
            SetCursorLocked(true);

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

        // ---------- Slide ----------
        bool wantsSlide = crouchPressed || (crouchHeld && sprintHeld && !IsCrouching);
        if (!IsSliding && grounded && wantsSlide && sprintHeld
            && moveInput.sqrMagnitude > 0.1f && Time.time >= nextSlideTime)
        {
            StartSlide(moveInput);
        }

        // ---------- Crouch ----------
        if (IsSliding || crouchHeld) IsCrouching = true;
        else if (IsCrouching && CanStand()) IsCrouching = false;

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
        if (grounded && verticalVelocity < 0f) verticalVelocity = -2f;
        verticalVelocity -= gravity * dt;

        controller.Move((horizontalVelocity + Vector3.up * verticalVelocity) * dt);

        UpdateHeight(dt);

        // Send tilstand til de andre (NetworkVariable sender kun ved ændring)
        if (netCrouching.Value != IsCrouching) netCrouching.Value = IsCrouching;
        if (netSliding.Value != IsSliding) netSliding.Value = IsSliding;
    }

    // ---------------- Kamera ----------------

    private void HandleLook(Mouse mouse)
    {
        Vector2 delta = mouse.delta.ReadValue() * mouseSensitivity;
        transform.Rotate(Vector3.up, delta.x);

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
        float speed = Mathf.Lerp(slideStartSpeed, slideEndSpeed, t * t);
        horizontalVelocity = slideDirection * speed;

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