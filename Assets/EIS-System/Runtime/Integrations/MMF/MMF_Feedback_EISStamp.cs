using System.Collections;
using UnityEngine;
using UnityEngine.Scripting.APIUpdating;

namespace MoreMountains.Feedbacks
{
    [AddComponentMenu("")]
    [FeedbackHelp("Requests EIS stamps from an MMF Player. Supports duration-based emission with cooldown and size/strength curves.")]
    [MovedFrom(false, null, "MoreMountains.Feedbacks")]
    [System.Serializable]
    [FeedbackPath("Environment/EIS Stamp")]
    public class MMF_Feedback_EISStamp : MMF_Feedback
    {
        public enum DirectionSources
        {
            TargetForward = 0,
            WorldDirection = 1,
            Velocity = 2,
        }

        public static bool FeedbackTypeAuthorized = true;

#if UNITY_EDITOR
        public override Color FeedbackColor => MMFeedbacksInspectorColors.RendererColor;
        public override bool EvaluateRequiresSetup() => (Baker == null) || (Preset == null);
        public override string RequiredTargetText => (Baker != null) ? Baker.name : "None";
        public override string RequiresSetupText => "This feedback requires Baker and Preset to be set.";
#endif

        [MMFInspectorGroup("EIS Stamp", true, 80, true)]
        [Tooltip("Target InteractionMapBakerV2. If null and AutoFindBaker is true, it will search once on initialization.")]
        public InteractionMapBakerV2 Baker;
        [Tooltip("Stamp preset used by RequestStamp.")]
        public EISStampPreset Preset;
        [Tooltip("Optional transform source. If null, MMF Player transform is used.")]
        public Transform TargetOverride;
        [Tooltip("If true and Baker is null, finds first active InteractionMapBakerV2 on initialization.")]
        public bool AutoFindBaker = true;

        [MMFInspectorGroup("Emission", true, 81)]
        [Min(0f)] public float Duration = 0.5f;
        [Tooltip("If false and this feedback is already emitting, subsequent plays are ignored until done.")]
        public bool AllowAdditivePlays = false;

        [MMFInspectorGroup("Stamp", true, 82)]
        public DirectionSources DirectionSource = DirectionSources.TargetForward;
        public Vector3 WorldDirection = Vector3.forward;
        [Min(0f)] public float SizeMultiplier = 1f;
        [Min(0f)] public float StrengthMultiplier = 1f;

        [MMFInspectorGroup("Arc", true, 82)]
        public bool UseArcMask = false;
        [Range(0f, 360f)] public float ArcAngle = 30f;
        [Min(0f)] public float ArcSoftness = 4f;
        public bool UseArcAngleCurve = false;
        public AnimationCurve ArcAngleCurve = AnimationCurve.Linear(0f, 1f, 1f, 1f);

        [MMFInspectorGroup("Curve", true, 83)]
        [Tooltip("If enabled, SizeMultiplier is multiplied by SizeMultiplierCurve over normalized emission time (0..1).")]
        public bool UseSizeCurve = true;
        public AnimationCurve SizeMultiplierCurve = AnimationCurve.Linear(0f, 1f, 1f, 1f);
        [Tooltip("If enabled, StrengthMultiplier is multiplied by StrengthMultiplierCurve over normalized emission time (0..1).")]
        public bool UseStrengthCurve = true;
        public AnimationCurve StrengthMultiplierCurve = AnimationCurve.Linear(0f, 1f, 1f, 1f);

        [MMFInspectorGroup("Throttling", true, 84)]
        public bool UseCooldown = true;
        [Min(0f)] public float CooldownSeconds = 0.03f;
        [Min(0f)] public float MinDistance = 0f;

        [MMFInspectorGroup("Debug", true, 85)]
        public bool VerboseLog = false;

        protected float _lastEmitTime = -999f;
        protected Vector3 _lastEmitPosition;
        protected bool _hasLastEmitPosition;
        protected Vector3 _lastDirection;
        protected Vector3 _lastSourcePosition;
        protected bool _hasLastSourcePosition;

        protected Coroutine _emitCoroutine;

        public override float FeedbackDuration
        {
            get => ApplyTimeMultiplier(Duration);
            set => Duration = Mathf.Max(0f, value);
        }

        protected override void CustomInitialization(MMF_Player owner)
        {
            base.CustomInitialization(owner);

            if ((Baker == null) && AutoFindBaker)
            {
                Baker = Object.FindFirstObjectByType<InteractionMapBakerV2>();
            }
        }

        protected override void CustomPlayFeedback(Vector3 position, float feedbacksIntensity = 1.0f)
        {
            if (!Active || !FeedbackTypeAuthorized)
            {
                return;
            }

            if ((Baker == null) || (Preset == null))
            {
                return;
            }

            if ((_emitCoroutine != null) && !AllowAdditivePlays)
            {
                return;
            }

            if (_emitCoroutine != null)
            {
                Owner.StopCoroutine(_emitCoroutine);
                _emitCoroutine = null;
            }

            float runDuration = Mathf.Max(0f, FeedbackDuration);
            if (runDuration <= 0.0001f)
            {
                EmitStamp(position, feedbacksIntensity, 1f);
                return;
            }

            _emitCoroutine = Owner.StartCoroutine(EmitOverTime(position, feedbacksIntensity, runDuration));
        }

        protected override void CustomStopFeedback(Vector3 position, float feedbacksIntensity = 1.0f)
        {
            base.CustomStopFeedback(position, feedbacksIntensity);

            if (_emitCoroutine != null)
            {
                Owner.StopCoroutine(_emitCoroutine);
                _emitCoroutine = null;
            }
        }

        protected IEnumerator EmitOverTime(Vector3 fallbackPosition, float feedbacksIntensity, float runDuration)
        {
            float start = GetNow();
            float end = start + runDuration;

            while (true)
            {
                float now = GetNow();
                float t = Mathf.Clamp01((now - start) / runDuration);

                EmitStamp(fallbackPosition, feedbacksIntensity, t);

                if (now >= end)
                {
                    break;
                }

                yield return null;
            }

            _emitCoroutine = null;
        }

        protected void EmitStamp(Vector3 fallbackPosition, float feedbacksIntensity, float normalizedTime)
        {
            Transform source = (TargetOverride != null) ? TargetOverride : Owner?.transform;
            Vector3 worldPos = (source != null) ? source.position : fallbackPosition;

            float now = GetNow();
            if (UseCooldown && ((now - _lastEmitTime) < CooldownSeconds))
            {
                return;
            }

            if (_hasLastEmitPosition && (MinDistance > 0f))
            {
                float sqrDist = (worldPos - _lastEmitPosition).sqrMagnitude;
                if (sqrDist < (MinDistance * MinDistance))
                {
                    return;
                }
            }

            Vector3 dir = ResolveDirection(source, worldPos);
            float intensity = Mathf.Max(0f, ComputeIntensity(feedbacksIntensity, worldPos));

            float sizeCurve = UseSizeCurve ? Mathf.Max(0f, SizeMultiplierCurve.Evaluate(normalizedTime)) : 1f;
            float strengthCurve = UseStrengthCurve ? Mathf.Max(0f, StrengthMultiplierCurve.Evaluate(normalizedTime)) : 1f;

            float finalSize = Mathf.Max(0f, SizeMultiplier * sizeCurve);
            float finalStrength = Mathf.Max(0f, StrengthMultiplier * strengthCurve * intensity);

            bool useArcMask = UseArcMask || (Preset != null && Preset.useArcMask);
            float arcAngle = UseArcMask ? ArcAngle : ((Preset != null) ? Preset.arcAngle : 30f);
            float arcSoftness = UseArcMask ? ArcSoftness : ((Preset != null) ? Preset.arcSoftness : 4f);
            if (UseArcMask && UseArcAngleCurve)
            {
                float arcCurve = Mathf.Max(0f, ArcAngleCurve.Evaluate(normalizedTime));
                arcAngle *= arcCurve;
            }

            Transform root = (source != null) ? source.root : ((Owner != null && Owner.transform != null) ? Owner.transform.root : null);
            Vector3 arcForward = (root != null) ? SafeNormalize(root.forward, dir) : dir;

            Baker.RequestStamp(worldPos, dir, finalSize, finalStrength, Preset, useArcMask, arcAngle, arcSoftness, arcForward);

            _lastEmitTime = now;
            _lastEmitPosition = worldPos;
            _hasLastEmitPosition = true;

            if (VerboseLog)
            {
                Debug.Log($"[MMF_EISStamp] t={normalizedTime:F3} pos={worldPos} dir={dir} size={finalSize:F3} strength={finalStrength:F3} arc={useArcMask} angle={arcAngle:F1}");
            }
        }

        protected virtual Vector3 ResolveDirection(Transform source, Vector3 worldPos)
        {
            switch (DirectionSource)
            {
                case DirectionSources.WorldDirection:
                    return SafeNormalize(WorldDirection, Vector3.forward);

                case DirectionSources.Velocity:
                    if (_hasLastSourcePosition)
                    {
                        Vector3 delta = worldPos - _lastSourcePosition;
                        _lastSourcePosition = worldPos;
                        if (delta.sqrMagnitude > 0.000001f)
                        {
                            _lastDirection = delta.normalized;
                            return _lastDirection;
                        }
                    }

                    _lastSourcePosition = worldPos;
                    _hasLastSourcePosition = true;
                    if (_lastDirection.sqrMagnitude > 0.000001f)
                    {
                        return _lastDirection;
                    }

                    return (source != null) ? SafeNormalize(source.forward, Vector3.forward) : Vector3.forward;

                case DirectionSources.TargetForward:
                default:
                    if (source != null)
                    {
                        Vector3 fwd = SafeNormalize(source.forward, Vector3.forward);
                        _lastDirection = fwd;
                        return fwd;
                    }

                    return (_lastDirection.sqrMagnitude > 0.000001f) ? _lastDirection : Vector3.forward;
            }
        }

        protected float GetNow()
        {
            return InScaledTimescaleMode ? Time.time : Time.unscaledTime;
        }

        protected static Vector3 SafeNormalize(Vector3 v, Vector3 fallback)
        {
            if (v.sqrMagnitude <= 0.000001f)
            {
                return fallback;
            }

            return v.normalized;
        }
    }
}
