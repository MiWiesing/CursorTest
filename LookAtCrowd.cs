using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events; // <-- for UnityEvents

[RequireComponent(typeof(Animator))]
[DefaultExecutionOrder(10000)] // very late; also set Script Execution Order in Project Settings if needed
public class LookAtCrowd : MonoBehaviour
{
    // =========================
    // Existing settings (kept)
    // =========================

    public float minLook = 0.8f;
    public float maxLook = 2.0f;
    public float minCooldown = 1.5f;
    public float maxCooldown = 4.0f;
    public float tickInterval = 3.0f;

    [Header("Manual Override")]
    public bool forceLook = false;

    [Header("Head IK")]
    [Range(0f, 1f)] public float ikWeight = 0.8f;
    public float turnSpeedDeg = 240f;
    public float maxYaw = 70f;
    public float maxPitch = 35f;

    [Header("Target (Eye Center)")]
    [Tooltip("Drag the __VREyeCenter__ here if you want. If left empty, it will auto-find.")]
    public Transform targetOverride;
    [Tooltip("Name of the player root to search under.")]
    public string playerRootName = "PlayerAvatar";
    [Tooltip("Exact name of the eye center transform to find under player root.")]
    public string eyeCenterName = "__VREyeCenter__";
    [Tooltip("Only look when target is within this distance.")]
    public float targetMaxDistance = 6f;

    [Header("Smoothing")]
    public float easeTime = 0.2f;

    [Header("Order Overrides")]
    [Tooltip("If ON, apply a final bone rotation at end-of-frame so crowd systems cannot override it.")]
    public bool applyAtEndOfFrame = true;
    [Range(0f, 1f)]
    [Tooltip("Blend of our end-of-frame correction (0 = off, 1 = full).")]
    public float endOfFrameBlend = 1f;

    Coroutine _scheduledStopCo;

    // =========================================
    // Condition & player/peer time targets
    // =========================================

    public enum ConditionType { Negative, Positive }

    [Header("Condition (from Controllers/InstructorFadeController)")]
    [Tooltip("Auto-read from Controllers/InstructorFadeController.condition at Start().")]
    public ConditionType condition = ConditionType.Positive;
    [Tooltip("If true, skip auto-read and use the 'condition' above.")]
    public bool conditionOverride = false;

    [Header("Target Player-Look Share by Condition (0..1)")]
    [Range(0f, 1f)] public float negativePlayerShare = 0.70f;
    [Range(0f, 1f)] public float positivePlayerShare = 0.50f;

    [Header("Convergence (EMA for realized shares)")]
    [Tooltip("Higher = faster adaptation to recent behavior.")]
    [Range(0.0f, 1.0f)] public float shareEmaAlpha = 0.15f;

    // ================================
    // Peer (agent) look settings
    // ================================

    [Header("Peer (Agent) Look Settings")]
    [Tooltip("Tag of other characters to consider as peer gaze targets.")]
    public string agentTag = "Agent";
    [Tooltip("How far to search for agents to look at.")]
    public float peerDetectRadius = 8f;
    [Tooltip("Refresh rate (seconds) for scanning peer heads.")]
    public float peerScanInterval = 2f;

    [Tooltip("Look durations when looking at PLAYER (rand in range).")]
    public Vector2 playerLookDurationRange = new Vector2(1.2f, 2.5f);

    [Tooltip("Look durations when looking at PEERS (rand in range). Usually shorter than player looks.")]
    public Vector2 peerLookDurationRange = new Vector2(0.5f, 1.4f);

    [Tooltip("Cooldown after a PLAYER look (seconds, random in range).")]
    public Vector2 playerCooldownRange = new Vector2(1.0f, 2.5f);

    [Tooltip("Cooldown after a PEER look (seconds, random in range).")]
    public Vector2 peerCooldownRange = new Vector2(0.7f, 1.7f);

    [Tooltip("Probability of starting a gaze at all when we tick. If false, we idle until next tick.")]
    [Range(0f, 1f)] public float startGazeProbability = 0.8f;

    [Tooltip("Chance to go idle even if we decide to gaze (adds variability).")]
    [Range(0f, 1f)] public float idleNoiseChance = 0.1f;

    // ===================================
    // Eye-contact reaction setup (NEW)
    // ===================================

    [Header("Eye Contact Reactions")]
    [Tooltip("Minimum seconds between two eye-contact reactions (anti-spam).")]
    public float eyeContactCooldown = 1.0f;

    [Tooltip("Require at least this much time (s) already spent in the current look phase before reacting.")]
    public float minTimeIntoLookToReact = 0.15f;

    [Tooltip("Extra negative reaction probability (0..1) on top of constant negative response).")]
    [Range(0f, 1f)] public float negativeExtraProbability = 0.30f;
    [Range(0f, 1f)] public float positiveExtraProbability = 0.30f;

    [Tooltip("Called when condition is Positive and eye contact occurs (fires 100%).")]
    public UnityEvent OnEyeContactPositive;
    public UnityEvent OnEyeContactPositiveExtra;

    [Tooltip("Called when condition is Negative and eye contact occurs (fires 100%).")]
    public UnityEvent OnEyeContactNegativeConstant;
    public UnityEvent OnEyeContactNegativeExtra;

    // ============
    // Internals
    // ============

    Animator _anim;
    Transform _head;

    Transform _playerEye;   // resolved eye center
    Transform _fallbackCam; // Camera.main if needed

    // Peer heads cache
    readonly List<Transform> _peerHeads = new List<Transform>();
    float _lastPeerScanTime = -999f;

    // Scheduler state
    enum GazePhase { Idle, Player, Peer }
    GazePhase _phase = GazePhase.Idle;

    // Phase timing (for easing/UI)
    float _phaseTime = 0f;  // 0.._phaseDur
    float _phaseDur = 0f;

    // Absolute guard timestamps
    float _phaseEndsAt = 0f;       // when current look phase ends
    float _cooldownEndsAt = 0f;    // when idle cooldown ends

    // eye-contact reaction cooldown
    float _lastEyeContactTime = -999f;

    // rolling realized share (EMA)
    float _emaPlayerTime = 0f;
    float _emaTotalLookTime = 1e-5f;

    // desired share for current condition
    float TargetPlayerShare
    {
        get
        {
            switch (condition)
            {
                case ConditionType.Negative: return negativePlayerShare;
                case ConditionType.Positive: return positivePlayerShare;
            }
            return positivePlayerShare;
        }
    }

    // per-frame aim/ik (kept from your original)
    Vector3 _aim;
    Vector3 _ikPos;
    float _ikW;
    bool _applyIK;
    Coroutine _loopCo;
    Coroutine _eofCo;

    // cached desired for end-of-frame / external application
    bool _haveDesiredWorldDir;
    Vector3 _desiredWorldDir;
    float _desiredWeight;

    // the actual target transform selected for this phase
    Transform _currentTarget;

    void Awake()
    {
        _anim = GetComponent<Animator>();
        _head = (_anim && _anim.isHuman) ? _anim.GetBoneTransform(HumanBodyBones.Head) : null;

        TryAssignPlayerEye();
        TryAssignFallbackCamera();

        if (!conditionOverride)
            TryReadConditionFromControllers();
    }

    void Start()
    {
        // Start in Idle with a brief randomized cooldown-style delay so crowds desync a bit
        _phase = GazePhase.Idle;
        _phaseTime = 0f;
        _phaseDur = 0f;
        _phaseEndsAt = 0f;
        _cooldownEndsAt = Time.time + Random.Range(0.3f, 1.2f);
    }

    void OnEnable()
    {
        if (_loopCo == null) _loopCo = StartCoroutine(LookLoop());
        if (_eofCo == null) _eofCo = StartCoroutine(EndOfFrameApplier());
    }

    void OnDisable()
    {
        if (_loopCo != null) { StopCoroutine(_loopCo); _loopCo = null; }
        if (_eofCo != null) { StopCoroutine(_eofCo); _eofCo = null; }
        _applyIK = false;
        _ikW = 0f;
        _haveDesiredWorldDir = false;
        _currentTarget = null;
    }

    // --------------------
    // Condition resolution
    // --------------------
    void TryReadConditionFromControllers()
    {
        var go = GameObject.Find("Controllers");
        if (!go) return;

        // Expecting a component "InstructorFadeController" with a public string "condition"
        var ifc = go.GetComponent("InstructorFadeController");
        if (ifc == null) return;

        var t = ifc.GetType();
        var field = t.GetField("condition");
        string cond = null;

        if (field != null)
            cond = field.GetValue(ifc) as string;
        else
        {
            var prop = t.GetProperty("condition");
            if (prop != null) cond = prop.GetValue(ifc, null) as string;
        }

        if (!string.IsNullOrEmpty(cond))
        {
            switch (cond.Trim().ToLowerInvariant())
            {
                case "negative": condition = ConditionType.Negative; break;
                case "positive": condition = ConditionType.Positive; break;
            }
        }
    }

    // ----------------------
    // Player target resolving
    // ----------------------
    void TryAssignFallbackCamera()
    {
        _fallbackCam = Camera.main ? Camera.main.transform : null;
    }

    void TryAssignPlayerEye()
    {
        if (targetOverride)
        {
            _playerEye = targetOverride;
            return;
        }

        GameObject playerRoot = GameObject.Find(playerRootName);
        if (playerRoot)
        {
            _playerEye = FindChildRecursive(playerRoot.transform, eyeCenterName);
            if (_playerEye) return;
        }
        _playerEye = null;
    }

    Transform FindChildRecursive(Transform root, string nameExact)
    {
        if (root.name == nameExact) return root;
        for (int i = 0; i < root.childCount; i++)
        {
            var found = FindChildRecursive(root.GetChild(i), nameExact);
            if (found) return found;
        }
        return null;
    }

    // -------------------------
    // Peer target (agent heads)
    // -------------------------
    void ScanPeerHeadsIfNeeded()
    {
        if (Time.time - _lastPeerScanTime < peerScanInterval) return;
        _lastPeerScanTime = Time.time;

        _peerHeads.Clear();
        var agents = GameObject.FindGameObjectsWithTag(agentTag);
        foreach (var a in agents)
        {
            if (a == this.gameObject) continue; // skip self
            var an = a.GetComponent<Animator>();
            if (an != null && an.isHuman)
            {
                var h = an.GetBoneTransform(HumanBodyBones.Head);
                if (h != null)
                {
                    float d2 = (h.position - transform.position).sqrMagnitude;
                    if (d2 <= peerDetectRadius * peerDetectRadius)
                        _peerHeads.Add(h);
                }
            }
        }
    }

    Transform ChoosePeerTarget()
    {
        ScanPeerHeadsIfNeeded();
        if (_peerHeads.Count == 0) return null;

        // Pick the closest valid within view cone
        Transform best = null;
        float bestD2 = float.MaxValue;
        for (int i = 0; i < _peerHeads.Count; i++)
        {
            var h = _peerHeads[i];
            if (!h) continue;
            if (!IsWithinDistanceAndView(h.position, peerDetectRadius)) continue;

            float d2 = (h.position - _head.position).sqrMagnitude;
            if (d2 < bestD2)
            {
                bestD2 = d2;
                best = h;
            }
        }
        return best;
    }

    // ================================
    // Gaze scheduler (phases & shares)
    // ================================
    IEnumerator LookLoop()
    {
        var wait = new WaitForSeconds(tickInterval);

        while (true)
        {
            if (forceLook)
            {
                if (_playerEye == null) TryAssignPlayerEye();
                if (_fallbackCam == null) TryAssignFallbackCamera();

                Transform t = null;
                if (IsPlayerAvailable()) t = _playerEye;
                else if (_fallbackCam) t = _fallbackCam;

                _phase = (t != null) ? GazePhase.Player : GazePhase.Idle;
                _currentTarget = t;
                _phaseDur = Mathf.Max(0.5f, playerLookDurationRange.x);
                _phaseTime = 0f;
                _phaseEndsAt = Time.time + _phaseDur;

                yield return wait;
                continue;
            }

            // Refresh player fallback if needed
            if (_playerEye == null) TryAssignPlayerEye();
            if (_fallbackCam == null) TryAssignFallbackCamera();

            if (_phase == GazePhase.Idle)
            {
                // Enforce cooldown gate
                if (Time.time >= _cooldownEndsAt)
                {
                    // Try to start a new phase
                    PickNextPhase();

                    // If we remained Idle, add a small dwell to avoid thrash
                    if (_phase == GazePhase.Idle)
                    {
                        _cooldownEndsAt = Time.time + Random.Range(minCooldown, maxCooldown) * 0.5f;
                    }
                }
            }
            else
            {
                // Enforce look end by absolute time or invalid target
                bool expired = Time.time >= _phaseEndsAt;
                bool invalid = !IsCurrentTargetValid();

                if (expired || invalid)
                {
                    float cooldown = 0f;
                    if (_phase == GazePhase.Player)
                        cooldown = Random.Range(playerCooldownRange.x, playerCooldownRange.y);
                    else if (_phase == GazePhase.Peer)
                        cooldown = Random.Range(peerCooldownRange.x, peerCooldownRange.y);
                    else
                        cooldown = Random.Range(minCooldown, maxCooldown);

                    // Enter Idle + cooldown window
                    _phase = GazePhase.Idle;
                    _currentTarget = null;
                    _phaseTime = 0f;
                    _phaseDur = 0f;
                    _phaseEndsAt = 0f;
                    _cooldownEndsAt = Time.time + Mathf.Max(0f, cooldown);
                }
                else
                {
                    // Update EMA shares while the phase is ongoing
                    float dt = Mathf.Min(tickInterval, Mathf.Max(0f, _phaseEndsAt - Time.time));
                    if (dt > 0f)
                    {
                        float alpha = Mathf.Clamp01(shareEmaAlpha);
                        _emaTotalLookTime = (1f - alpha) * _emaTotalLookTime + alpha * (dt + 1e-5f);
                        if (_phase == GazePhase.Player)
                            _emaPlayerTime = (1f - alpha) * _emaPlayerTime + alpha * dt;
                    }
                }
            }

            yield return wait;
        }
    }

    void PickNextPhase()
    {
        // Random chance to idle anyway
        if (Random.value < idleNoiseChance || Random.value > startGazeProbability)
        {
            _phase = GazePhase.Idle;
            _currentTarget = null;
            _phaseDur = 0f;
            _phaseTime = 0f;
            _phaseEndsAt = 0f;
            return;
        }

        float realizedShare = Mathf.Clamp01(_emaPlayerTime / Mathf.Max(1e-5f, _emaTotalLookTime));
        float targetShare = Mathf.Clamp01(TargetPlayerShare);
        float deficit = targetShare - realizedShare;

        bool playerAvailable = IsPlayerAvailable();
        if (playerAvailable && deficit > 0f)
        {
            _phase = GazePhase.Player;
            _currentTarget = _playerEye != null ? _playerEye : _fallbackCam;
            _phaseDur = Random.Range(playerLookDurationRange.x, playerLookDurationRange.y);
            _phaseTime = 0f;
            _phaseEndsAt = Time.time + _phaseDur;
            return;
        }

        if (playerAvailable && Random.value < Mathf.Max(0.1f, targetShare * 0.3f))
        {
            _phase = GazePhase.Player;
            _currentTarget = _playerEye != null ? _playerEye : _fallbackCam;
            _phaseDur = Random.Range(playerLookDurationRange.x, playerLookDurationRange.y);
            _phaseTime = 0f;
            _phaseEndsAt = Time.time + _phaseDur;
            return;
        }

        var peer = ChoosePeerTarget();
        if (peer != null)
        {
            _phase = GazePhase.Peer;
            _currentTarget = peer;
            _phaseDur = Random.Range(peerLookDurationRange.x, peerLookDurationRange.y);
            _phaseTime = 0f;
            _phaseEndsAt = Time.time + _phaseDur;
            return;
        }

        if (playerAvailable)
        {
            _phase = GazePhase.Player;
            _currentTarget = _playerEye != null ? _playerEye : _fallbackCam;
            _phaseDur = Random.Range(playerLookDurationRange.x, playerLookDurationRange.y);
            _phaseTime = 0f;
            _phaseEndsAt = Time.time + _phaseDur;
        }
        else
        {
            _phase = GazePhase.Idle;
            _currentTarget = null;
            _phaseDur = 0f;
            _phaseTime = 0f;
            _phaseEndsAt = 0f;
        }
    }

    bool IsPlayerAvailable()
    {
        if (_playerEye == null) TryAssignPlayerEye();
        if (_playerEye == null)
        {
            if (_fallbackCam == null) TryAssignFallbackCamera();
            return _fallbackCam != null && IsWithinDistanceAndView(_fallbackCam.position, targetMaxDistance);
        }
        return IsWithinDistanceAndView(_playerEye.position, targetMaxDistance);
    }

    bool IsCurrentTargetValid()
    {
        if (_phase == GazePhase.Idle) return true;
        if (_currentTarget == null) return false;

        float maxDist = (_phase == GazePhase.Player) ? targetMaxDistance : peerDetectRadius;
        return IsWithinDistanceAndView(_currentTarget.position, maxDist);
    }

    bool IsWithinDistanceAndView(Vector3 worldPos, float maxDist)
    {
        if (_head == null) return false;
        float d2 = (transform.position - worldPos).sqrMagnitude;
        if (d2 > maxDist * maxDist) return false;

        // view cone via yaw/pitch clamp
        Vector3 headPos = _head.position;
        Vector3 dir = (worldPos - headPos).normalized;

        Transform parent = _head.parent;
        Vector3 baseFwd = parent ? parent.InverseTransformDirection(_head.forward) : _head.forward;
        Vector3 tgtLocal = parent ? parent.InverseTransformDirection(dir) : dir;

        Quaternion toLocal = Quaternion.FromToRotation(baseFwd, tgtLocal);
        Vector3 e = toLocal.eulerAngles;
        if (e.x > 180f) e.x -= 360f;
        if (e.y > 180f) e.y -= 360f;

        return Mathf.Abs(e.x) <= maxPitch && Mathf.Abs(e.y) <= maxYaw;
    }

    // =================
    // Per-frame driving
    // =================
    void LateUpdate()
    {
        if (!_anim || !_head) return;

        // Update _phaseTime for easing based on absolute timestamps
        if (_phase == GazePhase.Player || _phase == GazePhase.Peer)
        {
            float remaining = Mathf.Max(0f, _phaseEndsAt - Time.time);
            _phaseTime = Mathf.Clamp(_phaseDur - remaining, 0f, _phaseDur);
        }
        else
        {
            _phaseTime = 0f; // idle
        }

        // If in idle cooldown, do nothing
        bool inIdleCooldown = (_phase == GazePhase.Idle) && (Time.time < _cooldownEndsAt);
        if (inIdleCooldown && !forceLook)
        {
            _applyIK = false;
            _ikW = 0f;
            _aim = Vector3.zero;
            _haveDesiredWorldDir = false;
            _desiredWeight = 0f;
            return;
        }

        Transform useThis = null;

        if (forceLook)
        {
            useThis = _playerEye != null ? _playerEye : _fallbackCam;
        }
        else
        {
            if (_phase == GazePhase.Player)
            {
                useThis = (_playerEye != null) ? _playerEye : _fallbackCam;
            }
            else if (_phase == GazePhase.Peer)
            {
                useThis = _currentTarget;
            }
            else
            {
                useThis = null;
            }
        }

        if (useThis != null && IsWithinDistanceAndView(useThis.position,
                _phase == GazePhase.Peer ? peerDetectRadius : targetMaxDistance))
        {
            Vector3 headPos = _head.position;
            if (_aim == Vector3.zero) _aim = headPos + _head.forward * 2f;

            Vector3 desiredPos = useThis.position;
            Vector3 desiredDir = (desiredPos - headPos).normalized;
            desiredDir = ClampDir(desiredDir);

            Vector3 curDir = (_aim - headPos).normalized;

            float dist = Vector3.Distance(transform.position, useThis.position);
            float range = (_phase == GazePhase.Peer) ? peerDetectRadius : targetMaxDistance;
            float proximity = 1f - Mathf.Clamp01(dist / range);
            float turn = Mathf.Lerp(0.5f * turnSpeedDeg, turnSpeedDeg, proximity);
            float weight = Mathf.Lerp(0.4f * ikWeight, ikWeight, proximity);

            float maxDeg = Mathf.Max(1f, turn * Time.deltaTime);
            Quaternion delta = Quaternion.FromToRotation(curDir, desiredDir);
            delta = Quaternion.RotateTowards(Quaternion.identity, delta, maxDeg);
            Vector3 newDir = (delta * curDir).normalized;

            float aimDist = Mathf.Max(0.5f, Vector3.Distance(headPos, desiredPos));
            _aim = headPos + newDir * aimDist;

            float totalDur = Mathf.Max(0.01f, _phaseDur);
            float tInto = Mathf.Clamp01((_phaseDur <= 0f) ? 1f : _phaseTime / totalDur);

            float inF = 1f, outF = 1f;
            if (!forceLook && easeTime > 0f)
            {
                inF = Mathf.Clamp01((_phaseTime) / easeTime);
                outF = Mathf.Clamp01((totalDur - _phaseTime) / easeTime);
            }
            _ikW = (forceLook ? weight : (weight * Mathf.Min(inF, outF)));

            _ikPos = _aim;
            _applyIK = _ikW > 0f;

            _haveDesiredWorldDir = true;
            _desiredWorldDir = desiredDir;
            _desiredWeight = _ikW;
        }
        else
        {
            _applyIK = false;
            _ikW = 0f;
            _aim = Vector3.zero;

            _haveDesiredWorldDir = false;
            _desiredWeight = 0f;
        }
    }

    void OnAnimatorIK(int layerIndex)
    {
        if (!_anim) return;

        if (_applyIK)
        {
            _anim.SetLookAtWeight(_ikW, 0.2f, 0.9f, 1.0f, 0.5f);
            _anim.SetLookAtPosition(_ikPos);
            _applyIK = false;
        }
        else
        {
            _anim.SetLookAtWeight(0f);
        }
    }

    IEnumerator EndOfFrameApplier()
    {
        var eof = new WaitForEndOfFrame();
        while (true)
        {
            yield return eof;

            if (!applyAtEndOfFrame || !_anim || !_head) continue;
            ApplyHeadOverrideNow();

            // IMPORTANT: do NOT modify _phaseTime here (avoids double-counting)
        }
    }

    /// <summary>
    /// Public hook to force-apply the cached head look now (call from MM OnUpdateGaze if needed).
    /// </summary>
    public void ApplyHeadOverrideNow()
    {
        if (!_anim || !_head) return;
        if (!_haveDesiredWorldDir || _desiredWeight <= 0f) return;

        float w = Mathf.Clamp01(_desiredWeight * endOfFrameBlend);
        if (w <= 0f) return;

        Vector3 clampedDir = ClampDir(_desiredWorldDir);
        Quaternion targetRot = Quaternion.LookRotation(clampedDir, Vector3.up);
        _head.rotation = Quaternion.Slerp(_head.rotation, targetRot, w);
    }

    Vector3 ClampDir(Vector3 worldDir)
    {
        if (_head == null) return worldDir;

        Transform parent = _head.parent;

        Vector3 baseFwd = parent ? parent.InverseTransformDirection(_head.forward) : _head.forward;
        Vector3 tgtLocal = parent ? parent.InverseTransformDirection(worldDir) : worldDir;

        Quaternion toLocal = Quaternion.FromToRotation(baseFwd, tgtLocal);
        Vector3 e = toLocal.eulerAngles;
        if (e.x > 180f) e.x -= 360f;
        if (e.y > 180f) e.y -= 360f;

        e.x = Mathf.Clamp(e.x, -maxPitch, maxPitch);
        e.y = Mathf.Clamp(e.y, -maxYaw, maxYaw);

        Quaternion clamped = Quaternion.Euler(e);
        Vector3 localDir = (clamped * baseFwd).normalized;
        return _head.parent ? _head.parent.TransformDirection(localDir) : localDir;
    }

#if UNITY_EDITOR
    void OnDrawGizmosSelected()
    {
        Gizmos.color = Color.cyan;
        Gizmos.DrawWireSphere(transform.position, targetMaxDistance);

        Gizmos.color = Color.yellow;
        Gizmos.DrawWireSphere(transform.position, peerDetectRadius);
    }
#endif

    [ContextMenu("Toggle Force Look")]
    void ToggleForceLook()
    {
        forceLook = !forceLook;
        if (forceLook)
        {
            _phase = GazePhase.Player;
            _currentTarget = _playerEye != null ? _playerEye : _fallbackCam;
            _phaseDur = Mathf.Max(1f, playerLookDurationRange.x);
            _phaseTime = 0f;
            _phaseEndsAt = Time.time + _phaseDur;
        }
    }

    [ContextMenu("Look For 2 Seconds (Player)")]
    void LookForTwoSeconds()
    {
        if (forceLook) return;
        _phase = GazePhase.Player;
        _currentTarget = _playerEye != null ? _playerEye : _fallbackCam;
        _phaseDur = 2f;
        _phaseTime = 0f;
        _phaseEndsAt = Time.time + 2f;
    }

    IEnumerator StopLookingAtPlayerAfterCo(float cooldown)
    {
        // Prevent forced look from re-asserting during the wait
        forceLook = false;

        if (cooldown > 0f) yield return new WaitForSeconds(cooldown);

        // Only stop if we are actually looking at the player at that moment
        if (_phase == GazePhase.Player)
        {
            // IMPORTANT: use default => applies Random(playerCooldownRange)
            BreakPlayerLook();
        }

        _scheduledStopCo = null;
    }

    // =====================================================
    // Eye-contact API — call from your player's gaze script
    // =====================================================

    /// <summary>
    /// schedule a stop after 'cooldown' seconds.
    /// If already scheduled, the previous one is replaced.
    /// </summary>
    public void ScheduleStopLookingAtPlayer(float cooldown)
    {
        if (_scheduledStopCo != null) StopCoroutine(_scheduledStopCo);
        _scheduledStopCo = StartCoroutine(StopLookingAtPlayerAfterCo(cooldown));
    }

    /// <summary>
    /// Minimal notification: call this when the player's gaze raycast hits this character.
    /// </summary>
    public void NotifyEyeContact()
    {
        TryFireEyeContactReactions();
    }

    /// <summary>
    /// Optional overload if you want to pass the player's gaze ray (not required).
    /// </summary>
    public void NotifyEyeContact(Ray playerGazeRay)
    {
        // you could add extra checks using the ray if desired
        TryFireEyeContactReactions();
    }

    // =====================================================
    // NEW: utility to break player look after extra negative
    // =====================================================
    void BreakPlayerLook(float cooldown = -1f)
    {
        if (cooldown < 0f)
            cooldown = Random.Range(playerCooldownRange.x, playerCooldownRange.y);

        // Switch out of player gaze and start an absolute cooldown window
        _phase = GazePhase.Idle;
        _currentTarget = null;

        _phaseDur = 0f;
        _phaseTime = 0f;
        _phaseEndsAt = 0f;
        _cooldownEndsAt = Time.time + Mathf.Max(0f, cooldown); // absolute cooldown gate

        // Clear any ongoing IK application
        _applyIK = false;
        _ikW = 0f;
        _aim = Vector3.zero;

        _haveDesiredWorldDir = false;
        _desiredWeight = 0f;
    }

    void TryFireEyeContactReactions()
    {
        Debug.Log($"Eye contact received! Phase: {_phase}, tInto: {_phaseTime:0.00}/{_phaseDur:0.00}, dtSinceLast: {Time.time - _lastEyeContactTime:0.00}");

        // Cooldown for reactions
        if (Time.time - _lastEyeContactTime < eyeContactCooldown)
        {
            Debug.Log("Eye contact skipped: reaction cooldown active");
            return;
        }

        if (_phase != GazePhase.Player)
        {
            Debug.Log($"Eye contact skipped: Not looking at player (phase is {_phase})");
            return;
        }

        // Require we are into the look at least a bit
        if (_phaseTime < minTimeIntoLookToReact)
        {
            Debug.Log($"Eye contact skipped: too early in look ({_phaseTime:0.00}s)");
            return;
        }

        if (!IsPlayerAvailable())
        {
            Debug.Log("Eye contact skipped: Player not available");
            return;
        }

        Debug.Log($"SUCCESS! Firing eye contact reaction for condition: {condition}");

        _lastEyeContactTime = Time.time;

        Transform useThis = (_playerEye != null) ? _playerEye : _fallbackCam;
        if (useThis != null && IsWithinDistanceAndView(useThis.position, 60.0f))
        {
            // Reactions by condition
            switch (condition)
            {
                case ConditionType.Positive:
                    OnEyeContactPositive?.Invoke(); // 100%
                    if (Random.value < positiveExtraProbability)
                    {
                        OnEyeContactPositiveExtra?.Invoke(); // extra hit
                        ScheduleStopLookingAtPlayer(6.0f);
                    }
                    ScheduleStopLookingAtPlayer(4.0f);
                    break;

                case ConditionType.Negative:
                    OnEyeContactNegativeConstant?.Invoke(); // 100%
                    if (Random.value < negativeExtraProbability)
                    {
                        OnEyeContactNegativeExtra?.Invoke(); // extra hit
                        ScheduleStopLookingAtPlayer(6.0f);
                    }
                    ScheduleStopLookingAtPlayer(4.0f);
                    break;
            }
        }
    }
}
