using UnityEngine;
using System.Collections;

[RequireComponent(typeof(Rigidbody2D))]
[RequireComponent(typeof(BoxCollider2D))]
public class EnemySmartAI : MonoBehaviour
{
    private enum EnemyState { Idle, Patrol, Chase, Suspicious, ReturnToPatrol }

    #region Movement & Physics
    [Header("⚙️ Movement")]
    public float patrolSpeed = 2f;
    public float chaseSpeed = 5f;
    public float maxSpeed => state == EnemyState.Chase ? chaseSpeed : patrolSpeed;

    [Tooltip("Seberapa cepat enemy mencapai kecepatan maksimal (semakin kecil = semakin smooth)")]
    public float acceleration = 8f;
    [Tooltip("Seberapa cepat enemy berhenti/melambat")]
    public float deceleration = 10f;

    public float jumpForce = 7f;
    public float jumpCooldown = 0.5f;
    private float lastJumpTime;

    [Tooltip("Enemy akan melompat jika dinding lebih rendah dari nilai ini")]
    public float maxJumpableWallHeight = 1.2f;
    #endregion

    #region Detection & AI
    [Header("🎯 Detection")]
    public float chaseStartRadius = 6f;
    public float chaseStopRadius = 9f;
    public float closeRangeThreshold = 1.5f;

    [Tooltip("Durasi enemy masih 'ingat' posisi player setelah player keluar dari view")]
    public float memoryDuration = 2.5f;
    private float lastSeenPlayerTime;
    private Vector3 lastKnownPlayerPosition;

    [Tooltip("Kemampuan enemy memprediksi arah lari player (0 = tidak prediksi, 1 = prediksi penuh)")]
    [Range(0f, 1f)] public float predictionFactor = 0.6f;

    [Tooltip("Variasi acak pada kecepatan patrol agar tidak terlihat robotik")]
    public float patrolSpeedVariation = 0.3f;
    private float currentPatrolSpeed;

    [Tooltip("Kemungkinan enemy berhenti sejenak saat patrol (untuk efek 'melihat-lihat')")]
    [Range(0f, 1f)] public float patrolPauseChance = 0.15f;
    [Tooltip("Durasi pause saat patrol")]
    public Vector2 patrolPauseDuration = new Vector2(0.8f, 2.5f);
    private bool isPatrolPaused;
    private float patrolPauseEndTime;
    #endregion

    #region Raycast & Environment
    [Header("📡 Raycast Settings")]
    public LayerMask groundLayer;
    public LayerMask obstacleLayer; // Untuk deteksi dinding/rintangan

    public float wallCheckDistance = 0.5f;
    public float groundCheckDistance = 1.5f;
    public float groundCheckForwardOffset = 0.4f;
    public float rayStartOffset = 0.05f;

    [Tooltip("Semakin cepat enemy bergerak, semakin jauh ia 'melihat' ke depan")]
    public float lookAheadSpeedMultiplier = 0.15f;
    #endregion

    #region Combat & Reaction
    [Header("⚔️ Combat")]
    public int damage = 1;
    public float damageCooldown = 1f;
    private float lastDamageTime;

    [Tooltip("Jika player melompat di atas enemy, enemy akan terpental (gaya Mario)")]
    public bool canBeStomped = true;
    public float stompForce = 5f;
    public int stompDamageToEnemy = 1;
    private bool isStunned = false;
    private float stunDuration = 1.2f;
    private float stunEndTime;
    #endregion

    #region Polish & Feedback
    [Header("✨ Polish")]
    public bool enableAnimationTriggers = true;
    public bool enableSoundTriggers = true;

    [Tooltip("Efek partikel saat enemy melompat (opsional)")]
    public GameObject jumpParticle;

    private float currentSpeed; // Untuk interpolasi gerakan smooth
    private int direction = 1; // 1 = kanan, -1 = kiri
    private int visualDirection = 1; // Untuk flip sprite terpisah dari logika
    #endregion

    // Components
    private Rigidbody2D rb;
    private BoxCollider2D boxCollider;
    private SpriteRenderer spriteRenderer;
    private Animator animator; // Opsional
    private Transform player;

    // State Management
    private EnemyState state = EnemyState.Patrol;
    private bool isGrounded;
    private bool wallAhead;
    private bool groundAhead;
    private bool ceilingAhead;
    private float lastTurnTime;
    private float lastDirectionChangeTime;

    [Header("🔧 Smoothing")]
    public float turnCooldown = 0.35f; // ⬅️ TAMBAHKAN INI
    public float directionChangeBuffer = 0.15f;
    public float velocitySmoothing = 0.1f;

    private void Awake()
    {
        rb = GetComponent<Rigidbody2D>();
        boxCollider = GetComponent<BoxCollider2D>();
        spriteRenderer = GetComponent<SpriteRenderer>();
        animator = GetComponent<Animator>(); // Opsional, tidak error jika null

        // Setup fisika
        rb.gravityScale = 3f;
        rb.freezeRotation = true;
        rb.interpolation = RigidbodyInterpolation2D.Interpolate;
        rb.collisionDetectionMode = CollisionDetectionMode2D.Continuous;
        if (boxCollider != null) boxCollider.isTrigger = false;
    }

    private void Start()
    {
        FindPlayer();
        currentPatrolSpeed = patrolSpeed;
        currentSpeed = 0f;

        // Mulai patrol dengan variasi awal
        InvokeRepeating(nameof(RandomizePatrolSpeed), 0f, 3f);
    }

    private void Update()
    {
        if (player == null) FindPlayer();

        UpdateStateLogic();
        UpdateVisualDirection();
        UpdateAnimationTriggers();

        // Debug: Tampilkan state di nama objek (hanya di Editor)
#if UNITY_EDITOR
        gameObject.name = $"Enemy_{state}";
#endif
    }

    private void FixedUpdate()
    {
        if (isStunned)
        {
            HandleStun();
            return;
        }

        // Tentukan arah tujuan berdasarkan state
        int targetDirection = CalculateTargetDirection();

        // Cek lingkungan dengan arah yang sesuai state
        int raycastDirection = (state == EnemyState.Chase || state == EnemyState.Suspicious)
            ? targetDirection : direction;
        CheckEnvironment(raycastDirection);

        // Jalankan behavior utama
        switch (state)
        {
            case EnemyState.Patrol:
                HandlePatrol();
                break;
            case EnemyState.Chase:
            case EnemyState.Suspicious:
                HandleChase(targetDirection);
                break;
            case EnemyState.ReturnToPatrol:
                HandleReturnToPatrol();
                break;
            case EnemyState.Idle:
                HandleIdle();
                break;
        }

        // Terapkan akselerasi/decelerasi untuk gerakan smooth
        ApplySmoothMovement(targetDirection);
    }

    #region State Logic
    private void UpdateStateLogic()
    {
        if (player == null)
        {
            if (state != EnemyState.Patrol) TransitionToState(EnemyState.Patrol);
            return;
        }

        float distanceToPlayer = Vector2.Distance(transform.position, player.position);
        bool playerInSight = CheckLineOfSight();

        switch (state)
        {
            case EnemyState.Patrol:
                if (playerInSight && distanceToPlayer <= chaseStartRadius)
                {
                    lastSeenPlayerTime = Time.time;
                    lastKnownPlayerPosition = player.position;
                    TransitionToState(EnemyState.Chase);
                }
                else if (Random.value < patrolPauseChance && !isPatrolPaused && CanTurn())
                {
                    StartPatrolPause();
                }
                break;

            case EnemyState.Chase:
                if (playerInSight)
                {
                    lastSeenPlayerTime = Time.time;
                    lastKnownPlayerPosition = player.position;
                }

                // Jika player keluar dari view, masuk mode "curiga" dulu
                if (!playerInSight && Time.time - lastSeenPlayerTime > 0.5f)
                {
                    TransitionToState(EnemyState.Suspicious);
                }
                else if (distanceToPlayer >= chaseStopRadius && Time.time - lastSeenPlayerTime > memoryDuration)
                {
                    TransitionToState(EnemyState.ReturnToPatrol);
                }
                break;

            case EnemyState.Suspicious:
                // Masih ingat posisi terakhir player
                if (playerInSight || Vector2.Distance(transform.position, lastKnownPlayerPosition) <= 1f)
                {
                    if (playerInSight)
                    {
                        lastSeenPlayerTime = Time.time;
                        lastKnownPlayerPosition = player.position;
                    }
                    TransitionToState(EnemyState.Chase);
                }
                else if (Time.time - lastSeenPlayerTime > memoryDuration)
                {
                    TransitionToState(EnemyState.ReturnToPatrol);
                }
                break;

            case EnemyState.ReturnToPatrol:
                // Kembali ke titik patrol terakhir atau random
                if (distanceToPlayer <= chaseStartRadius * 0.7f)
                {
                    TransitionToState(EnemyState.Chase);
                }
                else if (isGrounded && Mathf.Abs(rb.linearVelocity.x) < 0.1f)
                {
                    // Sudah berhenti, kembali patrol
                    direction = Random.value > 0.5f ? 1 : -1;
                    TransitionToState(EnemyState.Patrol);
                }
                break;
        }
    }

    private void TransitionToState(EnemyState newState)
    {
        if (state == newState) return;

        EnemyState previousState = state;
        state = newState;

        // Reset variabel saat transisi
        if (newState == EnemyState.Patrol)
        {
            currentPatrolSpeed = GetRandomPatrolSpeed();
            isPatrolPaused = false;
        }
        else if (newState == EnemyState.Chase)
        {
            // Saat mulai chase, langsung update arah ke player
            lastKnownPlayerPosition = player.position;
        }

        // Trigger event/polish
        OnStateChange(previousState, newState);
    }
    #endregion

    #region Behavior Handlers
    private void HandlePatrol()
    {
        if (isPatrolPaused)
        {
            if (Time.time >= patrolPauseEndTime)
            {
                isPatrolPaused = false;
                currentPatrolSpeed = GetRandomPatrolSpeed();
            }
            rb.linearVelocity = new Vector2(0f, rb.linearVelocity.y);
            return;
        }

        // Balik arah jika ada halangan
        if ((wallAhead || !groundAhead || ceilingAhead) && CanTurn())
        {
            ChangeDirection();
        }

        // Tambahkan sedikit variasi gerakan agar tidak kaku
        float speedVariation = Mathf.Sin(Time.time * 2f + direction) * patrolSpeedVariation;
        float targetSpeed = currentPatrolSpeed + speedVariation;

        // Gerak dengan kecepatan yang sudah divariasikan
        currentSpeed = Mathf.MoveTowards(currentSpeed, direction * targetSpeed, acceleration * Time.fixedDeltaTime);
        rb.linearVelocity = new Vector2(currentSpeed, rb.linearVelocity.y);
    }

    private void HandleChase(int targetDir)
    {
        if (player == null) return;

        float horizontalDist = Mathf.Abs(player.position.x - transform.position.x);
        float verticalDist = Mathf.Abs(player.position.y - transform.position.y);

        // === CLOSE RANGE: Serang langsung, abaikan halangan kecil ===
        if (horizontalDist <= closeRangeThreshold)
        {
            currentSpeed = Mathf.MoveTowards(currentSpeed, targetDir * chaseSpeed * 1.2f, acceleration * 1.5f * Time.fixedDeltaTime);
            rb.linearVelocity = new Vector2(currentSpeed, rb.linearVelocity.y);
            return;
        }

        // === PREDIKSI GERAKAN PLAYER ===
        float predictedPlayerX = player.position.x;
        if (predictionFactor > 0f && player.GetComponent<Rigidbody2D>() != null)
        {
            float playerVelocityX = player.GetComponent<Rigidbody2D>().linearVelocity.x;
            predictedPlayerX += playerVelocityX * predictionFactor * 0.5f; // Prediksi 0.5 detik ke depan
        }
        int predictedDirection = (predictedPlayerX >= transform.position.x) ? 1 : -1;

        // === LOGIKA LOMPAT CERDAS ===
        bool shouldJump = false;

        // Lompat jika ada dinding yang bisa dilewati
        if (wallAhead && isGrounded && Time.time - lastJumpTime > jumpCooldown)
        {
            // Cek tinggi dinding dengan raycast vertikal
            float wallHeight = CheckWallHeight();
            if (wallHeight <= maxJumpableWallHeight)
            {
                shouldJump = true;
            }
        }

        // Lompat jika player di atas dan ada platform
        if (verticalDist > 1f && player.position.y > transform.position.y + 0.5f && isGrounded)
        {
            shouldJump = true;
        }

        // Lompat untuk menghindari jurang (proaktif)
        if (!groundAhead && isGrounded && Time.time - lastJumpTime > jumpCooldown)
        {
            // Cek apakah ada tanah di sisi lain jurang
            if (CheckGroundOnOtherSide())
            {
                shouldJump = true;
            }
        }

        if (shouldJump)
        {
            rb.linearVelocity = new Vector2(predictedDirection * chaseSpeed, jumpForce);
            lastJumpTime = Time.time;
            TriggerJumpEffects();
            return;
        }

        // === CEGAH JATUH KE JURANG ===
        if (!groundAhead && !wallAhead)
        {
            // Berhenti sejenak, jangan langsung jatuh
            currentSpeed = Mathf.MoveTowards(currentSpeed, 0f, deceleration * 1.5f * Time.fixedDeltaTime);
            rb.linearVelocity = new Vector2(currentSpeed, rb.linearVelocity.y);

            // Coba cari arah alternatif
            if (CanTurn() && !CheckGroundInDirection(-predictedDirection))
            {
                // Tidak ada jalan lain, tetap coba ke arah player
            }
            return;
        }

        // === VARIASI KECEPATAN CHASE ===
        // Semakin dekat player, semakin agresif
        float aggressionFactor = Mathf.Clamp01(1f - (horizontalDist / chaseStartRadius));
        float dynamicChaseSpeed = Mathf.Lerp(chaseSpeed * 0.8f, chaseSpeed * 1.3f, aggressionFactor);

        // Tambahkan "kegairahan" seiring waktu chase
        float chaseDuration = Time.time - lastSeenPlayerTime;
        dynamicChaseSpeed *= Mathf.Clamp(1f + chaseDuration * 0.05f, 1f, 1.5f);

        currentSpeed = Mathf.MoveTowards(currentSpeed, predictedDirection * dynamicChaseSpeed, acceleration * Time.fixedDeltaTime);
        rb.linearVelocity = new Vector2(currentSpeed, rb.linearVelocity.y);
    }

    private void HandleReturnToPatrol()
    {
        // Pelan-pelan kembali ke mode patrol
        float targetSpeed = patrolSpeed * 0.7f;
        currentSpeed = Mathf.MoveTowards(currentSpeed, direction * targetSpeed, deceleration * Time.fixedDeltaTime);
        rb.linearVelocity = new Vector2(currentSpeed, rb.linearVelocity.y);

        // Jika sudah cukup jauh dari player, kembali patrol normal
        if (player != null && Vector2.Distance(transform.position, player.position) >= chaseStopRadius * 1.2f)
        {
            TransitionToState(EnemyState.Patrol);
        }
    }

    private void HandleIdle()
    {
        rb.linearVelocity = new Vector2(0f, rb.linearVelocity.y);
        // Bisa tambahkan animasi idle di sini
    }

    private void HandleStun()
    {
        // Saat di-stomp, enemy terpental ke atas dan tidak bisa bergerak
        if (Time.time >= stunEndTime)
        {
            isStunned = false;
            // Setelah stun, bisa langsung hancur atau kembali patrol
            // Di sini kita buat kembali patrol
            TransitionToState(EnemyState.Patrol);
            return;
        }

        // Gravity tetap bekerja, tapi horizontal velocity dikurangi
        rb.linearVelocity = new Vector2(rb.linearVelocity.x * 0.9f, rb.linearVelocity.y);
    }
    #endregion

    #region Environment Checking
    private void CheckEnvironment(int checkDirection)
    {
        Bounds bounds = boxCollider.bounds;

        // === Cek Dinding ===
        Vector2 wallOrigin = new Vector2(
            bounds.center.x + checkDirection * (bounds.extents.x + rayStartOffset),
            bounds.center.y
        );
        wallAhead = Physics2D.Raycast(wallOrigin, Vector2.right * checkDirection, wallCheckDistance, obstacleLayer | groundLayer);

        // === Cek Tanah di Depan (Jurang) ===
        float dynamicGroundOffset = groundCheckForwardOffset + (Mathf.Abs(currentSpeed) * lookAheadSpeedMultiplier);
        Vector2 groundOrigin = new Vector2(
            bounds.center.x + checkDirection * (bounds.extents.x + dynamicGroundOffset),
            bounds.min.y + rayStartOffset
        );
        groundAhead = Physics2D.Raycast(groundOrigin, Vector2.down, groundCheckDistance, groundLayer);

        // === Cek Grounded (di bawah kaki) ===
        Vector2 groundedOrigin = new Vector2(bounds.center.x, bounds.min.y + rayStartOffset);
        isGrounded = Physics2D.Raycast(groundedOrigin, Vector2.down, groundCheckDistance, groundLayer);

        // === Cek Langit-langit (opsional, untuk hindari lompat mentok) ===
        Vector2 ceilingOrigin = new Vector2(bounds.center.x, bounds.max.y - rayStartOffset);
        ceilingAhead = Physics2D.Raycast(ceilingOrigin, Vector2.up, 0.3f, groundLayer);
    }

    private float CheckWallHeight()
    {
        // Raycast vertikal ke atas dari posisi dinding untuk ukur tinggi
        Bounds bounds = boxCollider.bounds;
        Vector2 wallTop = new Vector2(
            bounds.center.x + direction * (bounds.extents.x + rayStartOffset),
            bounds.max.y
        );

        RaycastHit2D hit = Physics2D.Raycast(wallTop, Vector2.up, maxJumpableWallHeight + 0.5f, groundLayer);
        return hit.distance;
    }

    private bool CheckGroundOnOtherSide()
    {
        // Cek apakah ada tanah di seberang jurang (untuk lompat proaktif)
        Bounds bounds = boxCollider.bounds;
        float jumpDistance = 2f; // Asumsi jarak lompat maksimal

        Vector2 checkPos = new Vector2(
            transform.position.x + direction * (bounds.extents.x + jumpDistance),
            transform.position.y - 0.5f
        );

        return Physics2D.OverlapCircle(checkPos, 0.3f, groundLayer) != null;
    }

    private bool CheckGroundInDirection(int dir)
    {
        Bounds bounds = boxCollider.bounds;
        Vector2 origin = new Vector2(
            bounds.center.x + dir * (bounds.extents.x + groundCheckForwardOffset),
            bounds.min.y + rayStartOffset
        );
        return Physics2D.Raycast(origin, Vector2.down, groundCheckDistance, groundLayer);
    }

    private bool CheckLineOfSight()
    {
        if (player == null) return false;

        Vector2 directionToPlayer = (player.position - transform.position).normalized;
        float distanceToPlayer = Vector2.Distance(transform.position, player.position);

        // Raycast ke arah player
        RaycastHit2D hit = Physics2D.Raycast(
            transform.position + Vector3.up * 0.5f, // Mulai dari "mata" enemy
            directionToPlayer,
            distanceToPlayer,
            obstacleLayer | groundLayer
        );

        // Jika tidak ada halangan atau yang kena justru player
        return hit.collider == null || hit.transform.CompareTag("Player");
    }
    #endregion

    #region Movement Helpers
    private int CalculateTargetDirection()
    {
        if (player == null) return direction;

        if (state == EnemyState.Chase || state == EnemyState.Suspicious)
        {
            // Gunakan posisi terakhir yang diketahui jika player tidak terlihat
            Vector3 targetPos = (Time.time - lastSeenPlayerTime < memoryDuration)
                ? lastKnownPlayerPosition : player.position;

            return (targetPos.x >= transform.position.x) ? 1 : -1;
        }

        return direction;
    }

    private void ApplySmoothMovement(int targetDirection)
    {
        // Update visual direction terpisah dari logika movement
        if (Mathf.Abs(currentSpeed) > 0.1f)
        {
            visualDirection = currentSpeed > 0 ? 1 : -1;
        }

        // Update flip sprite
        if (spriteRenderer != null)
        {
            spriteRenderer.flipX = (visualDirection < 0);
        }
    }

    private void RandomizePatrolSpeed()
    {
        if (state == EnemyState.Patrol && !isPatrolPaused)
        {
            currentPatrolSpeed = GetRandomPatrolSpeed();
        }
    }

    private float GetRandomPatrolSpeed()
    {
        return patrolSpeed + Random.Range(-patrolSpeedVariation, patrolSpeedVariation);
    }

    private void StartPatrolPause()
    {
        isPatrolPaused = true;
        patrolPauseEndTime = Time.time + Random.Range(patrolPauseDuration.x, patrolPauseDuration.y);
    }

    private bool CanTurn()
    {
        return Time.time >= lastTurnTime + turnCooldown &&
               Time.time >= lastDirectionChangeTime + directionChangeBuffer;
    }

    private void ChangeDirection()
    {
        direction *= -1;
        lastTurnTime = Time.time;
        lastDirectionChangeTime = Time.time;
        currentPatrolSpeed = GetRandomPatrolSpeed(); // Variasi kecepatan setelah belok
    }

    private void UpdateVisualDirection()
    {
        // Update flip sprite berdasarkan arah gerakan aktual
        if (spriteRenderer != null && Mathf.Abs(rb.linearVelocity.x) > 0.05f)
        {
            int moveDirection = rb.linearVelocity.x > 0 ? 1 : -1;
            spriteRenderer.flipX = (moveDirection < 0);
        }
    }
    #endregion

    #region Combat & Collision
    private void OnCollisionStay2D(Collision2D collision)
    {
        if (!collision.gameObject.CompareTag("Player") || isStunned) return;
        if (Time.time < lastDamageTime + damageCooldown) return;

        // === MARIO-STYLE STOMP: Player jatuh dari atas ===
        if (canBeStomped)
        {
            Rigidbody2D playerRb = collision.gameObject.GetComponent<Rigidbody2D>();
            if (playerRb != null && playerRb.linearVelocity.y < -0.5f) // Player sedang jatuh
            {
                // Cek apakah player berada di atas enemy
                if (collision.contacts[0].normal.y > 0.7f) // Kontak dari atas
                {
                    // Enemy terpental, player memantul
                    playerRb.linearVelocity = new Vector2(playerRb.linearVelocity.x, stompForce);

                    // Enemy stunned atau hancur
                    isStunned = true;
                    stunEndTime = Time.time + stunDuration;

                    // Opsional: kurangi nyawa enemy, atau hancurkan
                    // Destroy(gameObject); // Jika ingin enemy langsung hilang

                    lastDamageTime = Time.time;
                    TriggerStompEffects();
                    return;
                }
            }
        }

        // === Damage Player (tabrakan samping) ===
        PlayerHealth playerHealth = collision.gameObject.GetComponent<PlayerHealth>();
        if (playerHealth != null)
        {
            playerHealth.TakeDamage(damage);
            lastDamageTime = Time.time;
        }
    }

    private void OnTriggerEnter2D(Collider2D other)
    {
        // Deteksi serangan dari player (opsional)
        if (other.CompareTag("PlayerAttack") && !isStunned)
        {
            // Enemy terpental ke belakang
            rb.linearVelocity = new Vector2(-direction * 3f, jumpForce * 0.7f);
            isStunned = true;
            stunEndTime = Time.time + stunDuration;
        }
    }
    #endregion

    #region Polish & Effects
    private void OnStateChange(EnemyState from, EnemyState to)
    {
        if (enableAnimationTriggers && animator != null)
        {
            animator.SetInteger("State", (int)to);
        }

        if (enableSoundTriggers)
        {
            // Placeholder: AudioSource audioSource = GetComponent<AudioSource>();
            // if (to == EnemyState.Chase) audioSource?.PlayOneShot(chaseSound);
        }
    }

    private void TriggerJumpEffects()
    {
        if (jumpParticle != null)
        {
            Instantiate(jumpParticle, transform.position + Vector3.down * 0.5f, Quaternion.identity);
        }
        if (enableSoundTriggers)
        {
            // AudioSource audioSource = GetComponent<AudioSource>();
            // audioSource?.PlayOneShot(jumpSound);
        }
    }

    private void TriggerStompEffects()
    {
        // Efek saat enemy di-stomp
        if (enableAnimationTriggers && animator != null)
        {
            animator.SetTrigger("Stomped");
        }
        if (enableSoundTriggers)
        {
            // AudioSource audioSource = GetComponent<AudioSource>();
            // audioSource?.PlayOneShot(stompSound);
        }
    }

    private void UpdateAnimationTriggers()
    {
        if (!enableAnimationTriggers || animator == null) return;

        animator.SetFloat("Speed", Mathf.Abs(rb.linearVelocity.x));
        animator.SetBool("IsGrounded", isGrounded);
        animator.SetFloat("VerticalVelocity", rb.linearVelocity.y);
    }
    #endregion

    #region Utilities
    private void FindPlayer()
    {
        GameObject playerObject = GameObject.FindGameObjectWithTag("Player");
        if (playerObject != null)
        {
            player = playerObject.transform;
        }
    }
    #endregion

    #region Debug & Gizmos
    private void OnDrawGizmosSelected()
    {
        if (!Application.isPlaying) return;

        Bounds bounds = boxCollider ? boxCollider.bounds : new Bounds(transform.position, Vector3.one);

        // Tentukan arah raycast berdasarkan state
        int rayDir = (state == EnemyState.Chase || state == EnemyState.Suspicious)
            ? CalculateTargetDirection() : direction;

        // Raycast Dinding (Merah)
        Gizmos.color = Color.red;
        Vector2 wallOrigin = new Vector2(
            bounds.center.x + rayDir * (bounds.extents.x + rayStartOffset),
            bounds.center.y
        );
        Gizmos.DrawLine(wallOrigin, wallOrigin + Vector2.right * rayDir * wallCheckDistance);

        // Raycast Tanah Depan (Hijau)
        Gizmos.color = Color.green;
        float dynamicOffset = groundCheckForwardOffset + (Mathf.Abs(currentSpeed) * lookAheadSpeedMultiplier);
        Vector2 groundOrigin = new Vector2(
            bounds.center.x + rayDir * (bounds.extents.x + dynamicOffset),
            bounds.min.y + rayStartOffset
        );
        Gizmos.DrawLine(groundOrigin, groundOrigin + Vector2.down * groundCheckDistance);

        // Raycast Grounded (Biru)
        Gizmos.color = Color.blue;
        Vector2 groundedOrigin = new Vector2(bounds.center.x, bounds.min.y + rayStartOffset);
        Gizmos.DrawLine(groundedOrigin, groundedOrigin + Vector2.down * groundCheckDistance);

        // Radius Detection
        Gizmos.color = Color.yellow; // Chase Start
        Gizmos.DrawWireSphere(transform.position, chaseStartRadius);
        Gizmos.color = Color.cyan; // Chase Stop
        Gizmos.DrawWireSphere(transform.position, chaseStopRadius);
        Gizmos.color = Color.magenta; // Close Range Attack
        Gizmos.DrawWireSphere(transform.position, closeRangeThreshold);

        // Memory Position (jika dalam mode curiga)
        if (state == EnemyState.Suspicious)
        {
            Gizmos.color = Color.white;
            Gizmos.DrawWireSphere(lastKnownPlayerPosition, 0.3f);
            Gizmos.DrawLine(transform.position, lastKnownPlayerPosition);
        }

        // Tampilkan state di atas enemy
        Gizmos.color = Color.white;
        Gizmos.DrawGUITexture(
            new Rect(Camera.main.WorldToScreenPoint(transform.position + Vector3.up * 2).x - 30,
                     Screen.height - Camera.main.WorldToScreenPoint(transform.position + Vector3.up * 2).y - 20,
                     60, 20),
            new Texture2D(1, 1) // Placeholder
        );
    }
    #endregion
}