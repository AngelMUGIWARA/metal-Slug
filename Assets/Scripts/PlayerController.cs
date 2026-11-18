using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Controlador 2D estilo run-and-gun: correr, saltar, agacharse y apuntar (adelante/arriba).
/// Usa el nuevo Input System del proyecto (Keyboard.current) y Rigidbody2D para la fisica.
/// </summary>
[RequireComponent(typeof(Rigidbody2D), typeof(BoxCollider2D))]
public class PlayerController : MonoBehaviour
{
    public enum AimDirection { Forward, Up }

    [Header("Movimiento")]
    [SerializeField] private float runSpeed = 6f;
    [SerializeField] private float acceleration = 60f;
    [SerializeField] private float airControl = 0.8f;
    [SerializeField] private float crouchSpeedMultiplier = 0.5f;

    [Header("Salto")]
    [SerializeField] private float jumpSpeed = 12f;
    [SerializeField] private float gravityScale = 3.5f;
    [SerializeField] private float fallGravityMultiplier = 1.7f;
    [SerializeField] private float coyoteTime = 0.1f;
    [SerializeField] private float jumpBufferTime = 0.12f;

    [Header("Deteccion de suelo")]
    [SerializeField] private Vector2 groundCheckSize = new Vector2(0.7f, 0.12f);
    [SerializeField] private LayerMask groundLayers;

    [Header("Paredes (evita pegarse)")]
    [SerializeField] private float wallCheckDistance = 0.05f;
    [SerializeField] private float wallCheckHeightOffset = 0.1f;
    [SerializeField] private float wallSlideSpeed = 0f;

    [Header("Agacharse")]
    [SerializeField] private float crouchColliderHeight = 0.55f;
    [SerializeField] private float crouchVisualScale = 0.6f;
    [SerializeField] private Transform visual;

    // --- Estado (solo lectura para el resto del juego) ---
    public AimDirection CurrentAim { get; private set; } = AimDirection.Forward;
    public Vector2 AimVector { get; private set; } = Vector2.right;
    public bool FacingRight { get; private set; } = true;
    public bool IsGrounded { get; private set; }
    public bool IsCrouching { get; private set; }
    public bool IsTouchingWall { get; private set; }

    // --- Internos ---
    private Rigidbody2D rb;
    private BoxCollider2D box;
    private float baseHeight;
    private float baseOffsetY;
    private int obstacleMask;   // todo menos la capa del propio jugador

    // Entrada: se lee en Update, la fisica se aplica en FixedUpdate
    private float moveInput;
    private bool jumpPressed;
    private bool crouchHeld;
    private bool aimUpHeld;
    private float coyoteCounter;
    private float jumpBufferCounter;

    private void Awake()
    {
        rb = GetComponent<Rigidbody2D>();
        box = GetComponent<BoxCollider2D>();

        // Fisica de plataforma: rapida, estable y sin volcar el objeto
        rb.gravityScale = gravityScale;
        rb.freezeRotation = true;
        rb.interpolation = RigidbodyInterpolation2D.Interpolate;
        rb.collisionDetectionMode = CollisionDetectionMode2D.Continuous;

        // Guardamos la forma "de pie" para poder volver desde la agachada
        baseHeight = box.size.y;
        baseOffsetY = box.offset.y;

        if (groundLayers.value == 0)
            groundLayers = LayerMask.GetMask("Ground");

        obstacleMask = ~(1 << gameObject.layer);

        if (visual == null)
        {
            Transform t = transform.Find("Visual");
            if (t != null) visual = t;
        }
    }

    private void Update()
    {
        ReadInput();

        // Temporizadores de salto (se pintan sueltos y no pierdes el salto)
        if (jumpPressed) jumpBufferCounter = jumpBufferTime;
        else jumpBufferCounter -= Time.deltaTime;

        if (IsGrounded) coyoteCounter = coyoteTime;
        else coyoteCounter -= Time.deltaTime;

        UpdateFacing();
        UpdateAim();
        UpdateCrouch();
    }

    private void FixedUpdate()
    {
        CheckGround();
        CheckWall();
        TryJump();
        ApplyMovement();
        ApplyExtraGravity();
    }

    // ------------------------------------------------------------------ ENTRADA
    private void ReadInput()
    {
        Keyboard kb = Keyboard.current;
        if (kb == null)
        {
            moveInput = 0f;
            jumpPressed = false;
            crouchHeld = false;
            aimUpHeld = false;
            return;
        }

        moveInput = 0f;
        if (kb.aKey.isPressed || kb.leftArrowKey.isPressed) moveInput -= 1f;
        if (kb.dKey.isPressed || kb.rightArrowKey.isPressed) moveInput += 1f;

        jumpPressed = kb.spaceKey.wasPressedThisFrame;
        crouchHeld = kb.sKey.isPressed || kb.downArrowKey.isPressed;
        aimUpHeld = kb.wKey.isPressed || kb.upArrowKey.isPressed;
    }

    // ------------------------------------------------------------------ ORIENTACION Y MIRA
    private void UpdateFacing()
    {
        if (moveInput > 0.01f) FacingRight = true;
        else if (moveInput < -0.01f) FacingRight = false;
        ApplyVisual();
    }

    private void UpdateAim()
    {
        CurrentAim = aimUpHeld ? AimDirection.Up : AimDirection.Forward;
        AimVector = CurrentAim == AimDirection.Up
            ? Vector2.up
            : (FacingRight ? Vector2.right : Vector2.left);
    }

    // ------------------------------------------------------------------ AGACHARSE
    private void UpdateCrouch()
    {
        if (crouchHeld && !IsCrouching) SetCrouch(true);
        else if (!crouchHeld && IsCrouching && CanStand()) SetCrouch(false);
    }

    private void SetCrouch(bool crouch)
    {
        IsCrouching = crouch;
        if (crouch)
        {
            // Bajamos el collider pero mantenemos los pies en el mismo sitio
            box.size = new Vector2(box.size.x, crouchColliderHeight);
            box.offset = new Vector2(box.offset.x, baseOffsetY - (baseHeight - crouchColliderHeight) * 0.5f);
        }
        else
        {
            box.size = new Vector2(box.size.x, baseHeight);
            box.offset = new Vector2(box.offset.x, baseOffsetY);
        }
        ApplyVisual();
    }

    private bool CanStand()
    {
        // Zona que ocuparia el torso al levantarse: si esta bloqueada, seguimos agachados
        float crouchTop = box.offset.y + box.size.y * 0.5f;
        float standTop = baseOffsetY + baseHeight * 0.5f;
        float h = standTop - crouchTop;
        if (h <= 0.001f) return true;

        float cy = crouchTop + h * 0.5f;
        Vector2 size = new Vector2(box.size.x * 0.9f, h);
        Vector2 center = (Vector2)transform.position + new Vector2(box.offset.x, cy);
        return Physics2D.OverlapBox(center, size, 0f, obstacleMask) == null;
    }

    private void ApplyVisual()
    {
        if (visual == null) return;

        float sx = FacingRight ? 1f : -1f;
        float sy = IsCrouching ? crouchVisualScale : 1f;
        visual.localScale = new Vector3(sx, sy, 1f);

        // Al encogerse, el sprite se escala desde el centro: lo bajamos para no "flotar"
        float spriteHeight = 1f;
        SpriteRenderer sr = visual.GetComponent<SpriteRenderer>();
        if (sr != null && sr.sprite != null) spriteHeight = sr.sprite.bounds.size.y;
        float shrink = (1f - sy) * spriteHeight * 0.5f;
        visual.localPosition = new Vector3(0f, -shrink, 0f);
    }

    // ------------------------------------------------------------------ FISICA
    private void CheckGround()
    {
        // Caja de deteccion justo bajo de los pies (2 cm por debajo del collider)
        Vector2 center = (Vector2)transform.position + new Vector2(
            box.offset.x,
            box.offset.y - box.size.y * 0.5f + groundCheckSize.y * 0.5f - 0.02f);
        IsGrounded = Physics2D.OverlapBox(center, groundCheckSize, 0f, groundLayers) != null;
    }

    private void CheckWall()
    {
        IsTouchingWall = false;
        if (Mathf.Abs(moveInput) < 0.01f) return;

        float dir = Mathf.Sign(moveInput);
        Vector2 origin = (Vector2)transform.position + new Vector2(
            box.offset.x + dir * box.size.x * 0.5f,
            box.offset.y + wallCheckHeightOffset);

        RaycastHit2D hit = Physics2D.Raycast(origin, new Vector2(dir, 0f), wallCheckDistance, obstacleMask);
        if (hit.collider != null) IsTouchingWall = true;
    }

    private void TryJump()
    {
        if (jumpBufferCounter > 0f && coyoteCounter > 0f && !IsCrouching)
        {
            rb.linearVelocity = new Vector2(rb.linearVelocity.x, jumpSpeed);
            jumpBufferCounter = 0f;
            coyoteCounter = 0f;
            IsGrounded = false;
        }
    }

    private void ApplyMovement()
    {
        float speed = runSpeed * (IsCrouching ? crouchSpeedMultiplier : 1f);
        float targetVx = moveInput * speed;

        // ANTI-PEGOTE: si hay pared en la direccion en la que empujas, anulo el avance.
        // Asi el jugador nunca se queda "pegado" a la pared: al dejar de empujar, cae.
        if (IsTouchingWall && Mathf.Abs(targetVx) > 0f)
        {
            targetVx = 0f;
            if (!IsGrounded && wallSlideSpeed > 0f && rb.linearVelocity.y < -wallSlideSpeed)
                rb.linearVelocity = new Vector2(0f, -wallSlideSpeed); // deslizamiento opcional
        }

        float accel = IsGrounded ? acceleration : acceleration * airControl;
        float vx = Mathf.MoveTowards(rb.linearVelocity.x, targetVx, accel * Time.fixedDeltaTime);
        rb.linearVelocity = new Vector2(vx, rb.linearVelocity.y);
    }

    private void ApplyExtraGravity()
    {
        // Gravedad mas fuerte al caer -> saltos con mejor "peso" y menos flotacion
        if (rb.linearVelocity.y < 0f)
        {
            Vector2 v = rb.linearVelocity;
            v.y += Physics2D.gravity.y * rb.gravityScale * (fallGravityMultiplier - 1f) * Time.fixedDeltaTime;
            rb.linearVelocity = v;
        }
    }

    // ------------------------------------------------------------------ GIZMOS
    private void OnDrawGizmosSelected()
    {
        if (box == null) box = GetComponent<BoxCollider2D>();
        if (box == null) return;

        Gizmos.color = Color.green;
        Vector2 groundCenter = (Vector2)transform.position + new Vector2(
            box.offset.x,
            box.offset.y - box.size.y * 0.5f + groundCheckSize.y * 0.5f - 0.02f);
        Gizmos.DrawWireCube(groundCenter, groundCheckSize);

        Gizmos.color = Color.yellow;
        Vector2 wallOrigin = (Vector2)transform.position + new Vector2(
            box.offset.x + box.size.x * 0.5f,
            box.offset.y + wallCheckHeightOffset);
        Gizmos.DrawLine(wallOrigin, wallOrigin + Vector2.right * wallCheckDistance);
        Gizmos.DrawLine(wallOrigin, wallOrigin + Vector2.left * wallCheckDistance);
    }
}
