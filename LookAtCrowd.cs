using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;

[RequireComponent(typeof(Animator))]
[DefaultExecutionOrder(10000)]
public class LookAtCrowd : MonoBehaviour
{
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

    [Header("Idle / Cooldown Timings")]
    [Tooltip("Randomized idle delay applied on enable so crowds desync a bit.")]
    public Vector2 initialIdleDelayRange = new Vector2(0.3f, 1.2f);
    [Tooltip("Cooldown window when we choose to remain idle.")]
    public Vector2 idleCooldownRange = new Vector2(0.6f, 1.4f);

    Coroutine _scheduledStopCo;

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

    [Tooltip("Probability of starting a gaze when we exit idle cooldown.")]
    [Range(0f, 1f)] public float startGazeProbability = 0.8f;

    [Tooltip("Additional noise that lowers the effective start chance.")]
    [Range(0f, 1f)] public float idleNoiseChance = 0.1f;

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

    Animator _anim;
    Transform _head;

    Transform _playerEye;
    Transform _fallbackCam;

    readonly List<Transform> _peerHeads = new List<Transform>();
    float _lastPeerScanTime = -999f;

    enum GazePhase { Idle, Player, Peer }
    GazePhase _phase = GazePhase.Idle;

    float _phaseTime = 0f;
    float _phaseDur = 0f;
    float _phaseStartTime = 0f;
    float _phaseEndsAt = 0f;
    float _cooldownEndsAt = 0f;

    float _lastEyeContactTime = -999f;

    float _emaPlayerShare = 0.5f;

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

    Vector3 _aim;
    Vector3 _ikPos;
    float _ikW;
    bool _applyIK;
    Coroutine _eofCo;

    bool _haveDesiredWorldDir;
    Vector3 _desiredWorldDir;
    float _desiredWeight;

    Transform _currentTarget;

    void Awake()
    {
        _anim = GetComponent<Animator>();
        _head = (_anim && _anim.isHuman) ? _anim.GetBoneTransform(HumanBodyBones.Head) : null;

        TryAssignPlayerEye();
        TryAssignFallbackCamera();

        if (!conditionOverride)
        {
            TryReadConditionFromControllers();
        }

        _emaPlayerShare = Mathf.Clamp01(TargetPlayerShare);
    }

    void Start()
    {
        _phase = GazePhase.Idle;
        _phaseTime = 0f;
        _phaseDur = 0f;
        _phaseStartTime = 0f;
        _phaseEndsAt = 0f;
        _cooldownEndsAt = Time.time + RandomRange(initialIdleDelayRange);
        ResetIKState();
    }

    void OnEnable()
    {
        if (_eofCo == null) _eofCo = StartCoroutine(EndOfFrameApplier());
    }

    void OnDisable()
    {
        if (_eofCo != null)
        {
            StopCoroutine(_eofCo);
            _eofCo = null;
        }

        if (_scheduledStopCo != null)
        {
            StopCoroutine(_scheduledStopCo);
            _scheduledStopCo = null;
        }

        ResetIKState();
        _currentTarget = null;
    }

    void TryReadConditionFromControllers()
    {
        var go = GameObject.Find("Controllers");
        if (!go) return;

        var ifc = go.GetComponent("InstructorFadeController");
        if (ifc == null) return;

        var t = ifc.GetType();
        var field = t.GetField("condition");
        string cond = null;

        if (field != null)
        {
            cond = field.GetValue(ifc) as string;
        }
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

    void ScanPeerHeadsIfNeeded()
    {
        if (Time.time - _lastPeerScanTime < peerScanInterval) return;
        _lastPeerScanTime = Time.time;

        _peerHeads.Clear();
        var agents = GameObject.FindGameObjectsWithTag(agentTag);
        foreach (var a in agents)
        {
            if (a == gameObject) continue;
            var an = a.GetComponent<Animator>();
            if (an != null && an.isHuman)
            {
                var h = an.GetBoneTransform(HumanBodyBones.Head);
                if (h != null)
                {
                    float d2 = (h.position - transform.position).sqrMagnitude;
                    if (d2 <= peerDetectRadius * peerDetectRadius)
                    {
                        _peerHeads.Add(h);
                    }
                }
            }
        }
    }

    Transform ChoosePeerTarget()
    {
        ScanPeerHeadsIfNeeded();
        if (_peerHeads.Count == 0) return null;

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

    void Update()
    {
        if (_playerEye == null) TryAssignPlayerEye();
        if (_fallbackCam == null) TryAssignFallbackCamera();

        if (forceLook)
        {
            EnsureForceLook();
        }
        else
        {
            RunScheduler();
            if (_phase == GazePhase.Player && float.IsPositiveInfinity(_phaseDur))
            {
                float duration = RandomRange(playerLookDurationRange);
                _phaseStartTime = Time.time;
                _phaseDur = duration;
                _phaseEndsAt = _phaseStartTime + _phaseDur;
            }
        }

        if (_phase == GazePhase.Player || _phase == GazePhase.Peer)
        {
            UpdateShareEMA(Time.deltaTime);
        }
    }

    void RunScheduler()
    {
        if (_phase == GazePhase.Idle)
        {
            if (Time.time >= _cooldownEndsAt)
            {
                TryStartNextPhase();
            }
            return;
        }

        bool invalid = !IsCurrentTargetValid();
        bool expired = !float.IsPositiveInfinity(_phaseEndsAt) && Time.time >= _phaseEndsAt;
        if (invalid || expired)
        {
            Vector2 cooldownRange = _phase == GazePhase.Player ? playerCooldownRange : peerCooldownRange;
            EnterIdleWithCooldown(RandomRange(cooldownRange));
        }
    }

    void TryStartNextPhase()
    {
        float finalStartChance = Mathf.Clamp01(startGazeProbability * (1f - idleNoiseChance));
        if (finalStartChance <= 0f || Random.value > finalStartChance)
        {
            EnterIdleWithCooldown(RandomRange(idleCooldownRange));
            return;
        }

        bool playerAvailable = IsPlayerAvailable();
        float targetShare = Mathf.Clamp01(TargetPlayerShare);
        float realizedShare = Mathf.Clamp01(_emaPlayerShare);
        float deficit = targetShare - realizedShare;

        if (playerAvailable && deficit > 0f)
        {
            StartPlayerLook(RandomRange(playerLookDurationRange));
            return;
        }

        if (playerAvailable && Random.value < Mathf.Max(0.1f, targetShare * 0.3f))
        {
            StartPlayerLook(RandomRange(playerLookDurationRange));
            return;
        }

        var peer = ChoosePeerTarget();
        if (peer != null)
        {
            StartPeerLook(peer, RandomRange(peerLookDurationRange));
            return;
        }

        if (playerAvailable)
        {
            StartPlayerLook(RandomRange(playerLookDurationRange));
        }
        else
        {
            EnterIdleWithCooldown(RandomRange(idleCooldownRange));
        }
    }

    void StartPlayerLook(float duration)
    {
        Transform target = _playerEye != null ? _playerEye : _fallbackCam;
        if (target == null)
        {
            EnterIdleWithCooldown(RandomRange(idleCooldownRange));
            return;
        }
        StartPhase(GazePhase.Player, target, duration);
    }

    void StartPeerLook(Transform peer, float duration)
    {
        if (peer == null)
        {
            EnterIdleWithCooldown(RandomRange(idleCooldownRange));
            return;
        }
        StartPhase(GazePhase.Peer, peer, duration);
    }

    void StartPhase(GazePhase newPhase, Transform target, float duration)
    {
        _phase = newPhase;
        _currentTarget = target;
        _phaseStartTime = Time.time;
        _phaseTime = 0f;
        bool infinite = float.IsPositiveInfinity(duration);
        _phaseDur = infinite ? float.PositiveInfinity : Mathf.Max(0f, duration);
        _phaseEndsAt = infinite ? float.PositiveInfinity : _phaseStartTime + _phaseDur;
        _cooldownEndsAt = 0f;
    }

    void EnsureForceLook()
    {
        Transform target = null;

        if (_playerEye != null && IsWithinDistanceAndView(_playerEye.position, targetMaxDistance))
        {
            target = _playerEye;
        }
        else if (_fallbackCam != null)
        {
            target = _fallbackCam;
        }

        if (target == null)
        {
            forceLook = false;
            EnterIdleWithCooldown(RandomRange(idleCooldownRange));
            return;
        }

        bool alreadyForced = _phase == GazePhase.Player &&
                             _currentTarget == target &&
                             float.IsPositiveInfinity(_phaseDur);

        if (!alreadyForced)
        {
            StartPhase(GazePhase.Player, target, float.PositiveInfinity);
        }
    }

    void EnterIdleWithCooldown(float cooldown)
    {
        _phase = GazePhase.Idle;
        _currentTarget = null;
        _phaseTime = 0f;
        _phaseDur = 0f;
        _phaseStartTime = 0f;
        _phaseEndsAt = 0f;
        _cooldownEndsAt = Time.time + Mathf.Max(0f, cooldown);
        ResetIKState();
    }

    float RandomRange(Vector2 range)
    {
        float min = Mathf.Min(range.x, range.y);
        float max = Mathf.Max(range.x, range.y);
        return Random.Range(min, max);
    }

    void UpdateShareEMA(float dt)
    {
        if (dt <= 0f) return;
        float baseAlpha = Mathf.Clamp01(shareEmaAlpha);
        if (baseAlpha <= 0f) return;

        float alpha = 1f - Mathf.Pow(1f - baseAlpha, dt);
        float sample = (_phase == GazePhase.Player) ? 1f : 0f;
        _emaPlayerShare = Mathf.Lerp(_emaPlayerShare, sample, alpha);
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

    void LateUpdate()
    {
        if (!_anim || !_head) return;

        if (_phase == GazePhase.Player || _phase == GazePhase.Peer)
        {
            _phaseTime = Mathf.Max(0f, Time.time - _phaseStartTime);
            if (!float.IsPositiveInfinity(_phaseDur))
            {
                _phaseTime = Mathf.Min(_phaseTime, _phaseDur);
            }
        }
        else
        {
            _phaseTime = 0f;
        }

        bool inIdleCooldown = (_phase == GazePhase.Idle) && (Time.time < _cooldownEndsAt);
        if (inIdleCooldown && !forceLook)
        {
            ResetIKState();
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

            float totalDur = float.IsPositiveInfinity(_phaseDur) ? Mathf.Max(_phaseTime, 0.01f) : Mathf.Max(0.01f, _phaseDur);

            float inF = 1f, outF = 1f;
            if (!forceLook && easeTime > 0f)
            {
                inF = Mathf.Clamp01(_phaseTime / easeTime);
                if (float.IsPositiveInfinity(_phaseDur))
                {
                    outF = 1f;
                }
                else
                {
                    outF = Mathf.Clamp01((_phaseDur - _phaseTime) / easeTime);
                }
            }
            _ikW = forceLook ? weight : (weight * Mathf.Min(inF, outF));

            _ikPos = _aim;
            _applyIK = _ikW > 0f;

            _haveDesiredWorldDir = true;
            _desiredWorldDir = desiredDir;
            _desiredWeight = _ikW;
        }
        else
        {
            ResetIKState();
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
        }
    }

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
            EnsureForceLook();
        }
    }

    [ContextMenu("Look For 2 Seconds (Player)")]
    void LookForTwoSeconds()
    {
        if (forceLook) return;
        Transform target = _playerEye != null ? _playerEye : _fallbackCam;
        if (target == null) return;

        StartPhase(GazePhase.Player, target, 2f);
    }

    IEnumerator StopLookingAtPlayerAfterCo(float cooldown)
    {
        forceLook = false;

        if (cooldown > 0f)
        {
            yield return new WaitForSeconds(cooldown);
        }

        if (_phase == GazePhase.Player)
        {
            BreakPlayerLook();
        }

        _scheduledStopCo = null;
    }

    public void ScheduleStopLookingAtPlayer(float cooldown)
    {
        if (_scheduledStopCo != null) StopCoroutine(_scheduledStopCo);
        _scheduledStopCo = StartCoroutine(StopLookingAtPlayerAfterCo(cooldown));

        if (_phase == GazePhase.Player)
        {
            float clamped = Mathf.Max(0f, cooldown);
            _phaseEndsAt = Time.time + clamped;
            if (_phaseStartTime <= 0f)
            {
                _phaseStartTime = Time.time;
            }
            _phaseDur = Mathf.Max(0f, _phaseEndsAt - _phaseStartTime);
        }
    }

    public void NotifyEyeContact()
    {
        TryFireEyeContactReactions();
    }

    public void NotifyEyeContact(Ray playerGazeRay)
    {
        TryFireEyeContactReactions();
    }

    void BreakPlayerLook(float cooldown = -1f)
    {
        float useCooldown = cooldown >= 0f ? cooldown : RandomRange(playerCooldownRange);
        EnterIdleWithCooldown(useCooldown);
    }

    void TryFireEyeContactReactions()
    {
        Debug.Log($"Eye contact received! Phase: {_phase}, tInto: {_phaseTime:0.00}/{_phaseDur:0.00}, dtSinceLast: {Time.time - _lastEyeContactTime:0.00}");

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
            switch (condition)
            {
                case ConditionType.Positive:
                    OnEyeContactPositive?.Invoke();
                    if (Random.value < positiveExtraProbability)
                    {
                        OnEyeContactPositiveExtra?.Invoke();
                        ScheduleStopLookingAtPlayer(6.0f);
                    }
                    ScheduleStopLookingAtPlayer(4.0f);
                    break;

                case ConditionType.Negative:
                    OnEyeContactNegativeConstant?.Invoke();
                    if (Random.value < negativeExtraProbability)
                    {
                        OnEyeContactNegativeExtra?.Invoke();
                        ScheduleStopLookingAtPlayer(6.0f);
                    }
                    ScheduleStopLookingAtPlayer(4.0f);
                    break;
            }
        }
    }

    void ResetIKState()
    {
        _applyIK = false;
        _ikW = 0f;
        _aim = Vector3.zero;
        _ikPos = Vector3.zero;
        _haveDesiredWorldDir = false;
        _desiredWeight = 0f;
    }
}
