using System;
using System.Collections.Generic;
using Games.Reefscape.Enums;
using Games.Reefscape.FieldScripts;
using Games.Reefscape.GamePieceSystem;
using Games.Reefscape.Robots;
using MoSimCore.Enums;
using RobotFramework.Controllers.Drivetrain;
using RobotFramework.Controllers.GamePieceSystem;
using UnityEngine;

namespace Prefabs.Reefscape.Robots.Mods.NYPowerhousePack._694
{
    /// <summary>
    /// 694's single custom auto align - handles reef branch scoring, the human player station, and the
    /// barge, the same way 340's GRRAutoAlign is one self-contained component rather than relying on the
    /// shared framework AutoAlign. It replaces the framework's ReefscapeAutoAlign component for this robot.
    ///
    /// Station and barge align both work the same way: an AlignZone is a corner-to-corner line (its
    /// rotation is the heading to face). Rotation and the distance perpendicular to that line are always
    /// PID-corrected, but the position *along* the line is a slider that starts centered the moment you
    /// engage and can be nudged toward either corner with the left stick, clamped so you can't slide past
    /// either end - "aligns to the middle, but lets you slide across it to either corner while it holds you
    /// at the right distance away," per the corresponding real StuyPulse commands
    /// (github.com/StuyPulse/Aunt-Mary): SwerveDrivePIDAssistToClosestCoralStation for the station, and
    /// SwerveDriveDriveAlignedToBarge118Score for the barge (which locks distance-to-line + heading and
    /// leaves the driver's left stick fully in control of position along the line - this is a clamped,
    /// centered version of that same idea rather than the real robot's unclamped open-ended slide).
    ///
    /// Barge align engages automatically while CurrentSetpoint is Barge and the driver is holding algae
    /// ready to score, while either AutoAlignLeft or AutoAlignRight is held - MoSim has no dedicated barge
    /// button, so this reuses the same "hold align" buttons the reef branch align uses, just routed to
    /// different behavior based on what setpoint you're currently in.
    ///
    /// Reef branch align keeps the exact same node-finding and offset-application logic the framework's
    /// ReefscapeAutoAlign used (closest ReefFace-tagged AlignNode, perspective-relative left/right,
    /// the same AutoAlignOffset assets already tuned for this robot) so existing tuned values keep working -
    /// it's just re-hosted here so the whole thing runs through one PID loop instead of two components
    /// fighting over the drivetrain.
    ///
    /// None of the three alignment modes use RobotFramework.Controllers.PidSystems.PIDController (the
    /// shared controller the joints are tuned through) - the translation/rotation PID loops below are
    /// implemented from scratch so a future change to that shared PID controller cannot change this
    /// component's behavior.
    ///
    /// This script doesn't hold its own references to the froggy coral stow / shooter algae stow
    /// GamePieceStates - it reads whether coral/algae is docked there through the sibling robot script's
    /// IStuyPulseGamePieceStatus instead, so that game-piece-system knowledge lives in one place.
    /// </summary>
    public class StuyPulseAutoAlign : MonoBehaviour
    {
        [Serializable]
        public class AlignZone
        {
            public Alliance alliance;

            [Tooltip("World-space position of one end of this align zone's line (e.g. one edge of the HP station opening, or one end of the barge line)")]
            public Vector3 leftCorner;

            [Tooltip("World-space position of the other end of this align zone's line")]
            public Vector3 rightCorner;

            [Tooltip("Robot heading (degrees) to face while aligned to this zone")]
            public float yRotation;
        }

        /// <summary>
        /// A small, self-contained PID axis. Deliberately separate from RobotFramework.Controllers.PidSystems.PIDController
        /// so this component's tuning/behavior is fully isolated from that shared class.
        /// </summary>
        [Serializable]
        public class ManualPidAxis
        {
            public float kP = 3f;
            public float kI = 0f;
            public float kD = 0f;
            [Tooltip("Clamps the accumulated integral term to prevent windup")]
            public float integralLimit = 0.5f;

            private float _integral;
            private float _lastError;
            private bool _hasLastError;

            public float Update(float error, float dt)
            {
                _integral = Mathf.Clamp(_integral + error * dt, -integralLimit, integralLimit);
                var derivative = _hasLastError && dt > 0f ? (error - _lastError) / dt : 0f;

                _lastError = error;
                _hasLastError = true;

                return kP * error + kI * _integral + kD * derivative;
            }

            public void Reset()
            {
                _integral = 0f;
                _hasLastError = false;
            }
        }

        [Header("Human Player Station Align")]
        [Tooltip("One entry per physical coral station on the field")]
        [SerializeField] private AlignZone[] stationTargets;

        [Tooltip("Only assist toward the station within this distance (feet)")]
        [SerializeField] private float maxStationAlignDistanceFeet = 12f;

        [Tooltip("How fast (world units/sec at full stick deflection) the slide target moves along the station line")]
        [SerializeField] private float stationSlideSpeed = 1.5f;

        [Header("Barge Align")]
        [Tooltip("One entry per alliance's barge line")]
        [SerializeField] private AlignZone[] bargeTargets;

        [Tooltip("Only assist toward the barge within this distance (feet)")]
        [SerializeField] private float maxBargeAlignDistanceFeet = 20f;

        [Tooltip("How fast (world units/sec at full stick deflection) the slide target moves along the barge line")]
        [SerializeField] private float bargeSlideSpeed = 2.5f;

        [Header("Reef Branch Align")]
        [SerializeField] private AutoAlignOffset l1offset;
        [SerializeField] private AutoAlignOffset frontLeftOffset;
        [SerializeField] private AutoAlignOffset frontRightOffset;
        [SerializeField] private AutoAlignOffset backLeftOffset;
        [SerializeField] private AutoAlignOffset backRightOffset;
        [SerializeField] private AutoAlignOffset frontLeftL4Offset;
        [SerializeField] private AutoAlignOffset frontRightL4Offset;
        [SerializeField] private AutoAlignOffset backLeftL4Offset;
        [SerializeField] private AutoAlignOffset backRightL4Offset;

        [Tooltip("Only assist toward the reef within this distance (feet)")]
        [SerializeField] private float maxReefAlignDistanceFeet = 15f;

        [Header("Manual PID (self-contained, not the shared framework PIDController)")]
        [SerializeField] private ManualPidAxis translateX = new ManualPidAxis();
        [SerializeField] private ManualPidAxis translateZ = new ManualPidAxis();
        [SerializeField] private ManualPidAxis rotate = new ManualPidAxis { kP = 1.5f, integralLimit = 1f };

        [Tooltip("Clamps the combined X/Z drive output to the same -1..1 range the joystick drive input uses")]
        [SerializeField] private float maxTranslateOutput = 1f;

        private const float FEET_TO_METERS = 0.3048f;
        private const float INCHES_TO_METERS = 0.0254f;
        private const float MIN_LINE_LENGTH = 0.01f;

        private ReefscapeRobotBase _stuyBase;
        private DriveController _driveController;
        private RobotGamePieceController<ReefscapeGamePiece, ReefscapeGamePieceData> _pieces;
        private IStuyPulseGamePieceStatus _gamePieceStatus;

        private readonly List<AlignNode> _reefFaces = new();
        private readonly Dictionary<Transform, AlignNode> _reefNodeParents = new();
        private Transform _closestReefNode;
        private Transform _secondClosestReefNode;

        private bool _stationEngaged;
        private float _stationSlide = 0.5f;

        private bool _bargeEngaged;
        private float _bargeSlide = 0.5f;

        private bool _stationAlignActive;
        private bool _bargeAlignActive;
        private bool _reefAlignActive;
        private bool _reefAlignLeft;

        private void Awake()
        {
            _stuyBase = GetComponent<ReefscapeRobotBase>();
            _driveController = GetComponent<DriveController>();
            _pieces = GetComponent<RobotGamePieceController<ReefscapeGamePiece, ReefscapeGamePieceData>>();
            _gamePieceStatus = GetComponent<IStuyPulseGamePieceStatus>();
        }

        private void Start()
        {
            foreach (var faceObject in GameObject.FindGameObjectsWithTag("ReefFace"))
            {
                if (!faceObject.TryGetComponent<AlignNode>(out var face)) continue;
                _reefFaces.Add(face);
                _reefNodeParents.TryAdd(face.LeftNode.transform, face);
                _reefNodeParents.TryAdd(face.RightNode.transform, face);
            }
        }

        private void Update()
        {
            if (_stuyBase == null) return;

            // Only re-pick the closest reef faces on the button press edge, same as the framework's
            // ReefscapeAutoAlign - stops the target from jumping to a different branch mid-align.
            if (_stuyBase.AutoAlignLeftAction.triggered || _stuyBase.AutoAlignRightAction.triggered)
            {
                (_closestReefNode, _secondClosestReefNode) = FindClosestReefNodes();
            }
        }

        private void FixedUpdate()
        {
            var wasActive = _stationAlignActive || _bargeAlignActive || _reefAlignActive;

            if (TryGetBargeAlignTarget(out var bargeTarget, out var bargeYaw))
            {
                _bargeAlignActive = true;
                _reefAlignActive = false;
                _stationAlignActive = false;
                DriveManualPid(bargeTarget, bargeYaw);
                return;
            }

            _bargeAlignActive = false;

            if (TryGetReefAlignTarget(out var reefTarget, out var reefYaw))
            {
                _reefAlignActive = true;
                _stationAlignActive = false;
                DriveManualPid(reefTarget, reefYaw);
                return;
            }

            _reefAlignActive = false;

            if (TryGetStationAlignTarget(out var stationTarget, out var stationYaw))
            {
                _stationAlignActive = true;
                DriveManualPid(stationTarget, stationYaw);
                return;
            }

            _stationAlignActive = false;

            if (wasActive) ResetPid();
        }

        // ---- Human player station ----

        private bool TryGetStationAlignTarget(out Vector3 targetPosition, out float targetYaw)
        {
            targetPosition = Vector3.zero;
            targetYaw = 0f;

            if (_stuyBase == null || _driveController == null || stationTargets == null || stationTargets.Length == 0)
            {
                _stationEngaged = false;
                return false;
            }

            if (_stuyBase.AutoAlignLeftAction.IsPressed() || _stuyBase.AutoAlignRightAction.IsPressed()) { _stationEngaged = false; return false; }
            if (_stuyBase.CurrentRobotMode != ReefscapeRobotMode.Coral) { _stationEngaged = false; return false; }
            if (_stuyBase.CurrentIntakeMode != ReefscapeIntakeMode.Normal) { _stationEngaged = false; return false; }
            if (!_stuyBase.IntakeAction.IsPressed()) { _stationEngaged = false; return false; }

            var coral = _pieces != null ? _pieces.GetPieceByName(ReefscapeGamePieceType.Coral.ToString()) : null;
            if (coral != null && coral.HasPiece()) { _stationEngaged = false; return false; }

            var zone = GetClosestZone(stationTargets, _stuyBase.Alliance);
            if (zone == null) { _stationEngaged = false; return false; }

            if (!TryGetZoneTarget(zone, maxStationAlignDistanceFeet, stationSlideSpeed, ref _stationEngaged, ref _stationSlide, out targetPosition))
            {
                return false;
            }

            targetYaw = zone.yRotation;
            return true;
        }

        // ---- Barge ----

        private bool TryGetBargeAlignTarget(out Vector3 targetPosition, out float targetYaw)
        {
            targetPosition = Vector3.zero;
            targetYaw = 0f;

            if (_stuyBase == null || _driveController == null || bargeTargets == null || bargeTargets.Length == 0)
            {
                _bargeEngaged = false;
                return false;
            }

            if (_stuyBase.CurrentSetpoint != ReefscapeSetpoints.Barge) { _bargeEngaged = false; return false; }
            if (!(_stuyBase.AutoAlignLeftAction.IsPressed() || _stuyBase.AutoAlignRightAction.IsPressed())) { _bargeEngaged = false; return false; }
            if (_gamePieceStatus == null || !_gamePieceStatus.HasShooterAlgae) { _bargeEngaged = false; return false; }

            var zone = GetClosestZone(bargeTargets, _stuyBase.Alliance);
            if (zone == null) { _bargeEngaged = false; return false; }

            if (!TryGetZoneTarget(zone, maxBargeAlignDistanceFeet, bargeSlideSpeed, ref _bargeEngaged, ref _bargeSlide, out targetPosition))
            {
                return false;
            }

            targetYaw = zone.yRotation;
            return true;
        }

        // ---- Shared corner-to-corner slide logic used by both station and barge align ----

        private bool TryGetZoneTarget(AlignZone zone, float maxDistanceFeet, float slideSpeed, ref bool engaged, ref float slide, out Vector3 targetPosition)
        {
            targetPosition = Vector3.zero;

            var lineLength = Vector3.Distance(zone.leftCorner, zone.rightCorner);
            if (lineLength < MIN_LINE_LENGTH)
            {
                engaged = false;
                return false;
            }

            var center = (zone.leftCorner + zone.rightCorner) * 0.5f;
            var robotXZ = new Vector2(transform.position.x, transform.position.z);
            var centerXZ = new Vector2(center.x, center.z);
            if (Vector2.Distance(robotXZ, centerXZ) > maxDistanceFeet * FEET_TO_METERS)
            {
                engaged = false;
                return false;
            }

            // Fresh engage (button/context just became true this frame) starts the slide back at the middle.
            if (!engaged) slide = 0.5f;
            engaged = true;

            var stick = _stuyBase.TranslateAction.ReadValue<Vector2>().x;
            slide = Mathf.Clamp01(slide + stick * slideSpeed * Time.fixedDeltaTime / lineLength);

            targetPosition = Vector3.Lerp(zone.leftCorner, zone.rightCorner, slide);
            return true;
        }

        private AlignZone GetClosestZone(AlignZone[] zones, Alliance alliance)
        {
            AlignZone closest = null;
            var closestDistance = float.MaxValue;
            var robotXZ = new Vector2(transform.position.x, transform.position.z);

            foreach (var zone in zones)
            {
                if (zone == null || zone.alliance != alliance) continue;

                var center = (zone.leftCorner + zone.rightCorner) * 0.5f;
                var distance = Vector2.Distance(robotXZ, new Vector2(center.x, center.z));

                if (distance < closestDistance)
                {
                    closestDistance = distance;
                    closest = zone;
                }
            }

            return closest;
        }

        // ---- Reef branch ----

        private bool TryGetReefAlignTarget(out Vector3 targetPosition, out float targetYaw)
        {
            targetPosition = Vector3.zero;
            targetYaw = 0f;
            _reefAlignLeft = false;

            if (_stuyBase == null || _driveController == null) return false;

            var pressedLeft = _stuyBase.AutoAlignLeftAction.IsPressed();
            var pressedRight = _stuyBase.AutoAlignRightAction.IsPressed();
            if (!pressedLeft && !pressedRight) return false;
            if (_stuyBase.CurrentSetpoint == ReefscapeSetpoints.Place) return false;
            if (_stuyBase.CurrentSetpoint == ReefscapeSetpoints.Barge) return false;

            var usePerspective = PlayerPrefs.GetInt("PerspectiveAutoAlign", 1) == 1;
            var cameraFacesLeftNode = usePerspective && _closestReefNode != null &&
                                       _reefNodeParents.TryGetValue(_closestReefNode, out var parentForCamera) &&
                                       CameraFacesNode(parentForCamera);

            // Perspective mode flips which physical side "left" refers to depending on which way the
            // camera is looking, same as the framework's ReefscapeAutoAlign.
            var wantsLeftSide = pressedLeft
                ? (usePerspective ? !cameraFacesLeftNode : true)
                : (usePerspective && cameraFacesLeftNode);

            if (TryAlignToReefNode(_closestReefNode, wantsLeftSide, out targetPosition, out targetYaw)) return true;
            if (TryAlignToReefNode(_secondClosestReefNode, wantsLeftSide, out targetPosition, out targetYaw)) return true;

            if (_closestReefNode != null && _reefNodeParents.TryGetValue(_closestReefNode, out var closestParent))
            {
                if (TryAlignToReefNode(closestParent.LeftNode.transform, wantsLeftSide, out targetPosition, out targetYaw)) return true;
                if (TryAlignToReefNode(closestParent.RightNode.transform, wantsLeftSide, out targetPosition, out targetYaw)) return true;
            }

            return false;
        }

        private bool TryAlignToReefNode(Transform node, bool wantsLeftSide, out Vector3 targetPosition, out float targetYaw)
        {
            targetPosition = Vector3.zero;
            targetYaw = 0f;

            if (node == null || !_reefNodeParents.TryGetValue(node, out var parent)) return false;

            var isCorrectSide = wantsLeftSide ? parent.LeftNode.transform == node : parent.RightNode == node.gameObject;
            if (!isCorrectSide) return false;

            if (Vector3.Distance(transform.position, node.position) > maxReefAlignDistanceFeet * FEET_TO_METERS) return false;

            var holdingFroggyCoral = _gamePieceStatus != null && _gamePieceStatus.HasFroggyCoral;

            var offset = holdingFroggyCoral ? l1offset : GetScoringOffset(wantsLeftSide);
            if (offset == null) return false;

            var facingReef = IsFacingReef();
            var targetRotation = node.rotation;
            if (!facingReef && !holdingFroggyCoral) targetRotation *= Quaternion.Euler(0, 180, 0);

            var localOffset = new Vector3(offset.xOffset, offset.yOffset, offset.zOffset) * INCHES_TO_METERS;
            targetPosition = node.position + targetRotation * localOffset;

            targetRotation *= Quaternion.Euler(0, offset.Rotation, 0);
            targetYaw = targetRotation.eulerAngles.y;

            _reefAlignLeft = wantsLeftSide;
            return true;
        }

        private AutoAlignOffset GetScoringOffset(bool isLeftSide)
        {
            var isL4 = _stuyBase.CurrentSetpoint == ReefscapeSetpoints.L4;
            var facingReef = IsFacingReef();

            if (facingReef) return isLeftSide ? (isL4 ? frontLeftL4Offset : frontLeftOffset) : (isL4 ? frontRightL4Offset : frontRightOffset);
            return isLeftSide ? (isL4 ? backLeftL4Offset : backLeftOffset) : (isL4 ? backRightL4Offset : backRightOffset);
        }

        private (Transform closest, Transform secondClosest) FindClosestReefNodes()
        {
            AlignNode closestFace = null;
            AlignNode secondClosestFace = null;
            var closestDist = float.MaxValue;
            var secondClosestDist = float.MaxValue;

            foreach (var face in _reefFaces)
            {
                if (face == null || face.transform == null) continue;

                var dist = Vector3.Distance(transform.position, face.transform.position);
                if (dist < closestDist)
                {
                    secondClosestDist = closestDist;
                    secondClosestFace = closestFace;
                    closestDist = dist;
                    closestFace = face;
                }
                else if (dist < secondClosestDist)
                {
                    secondClosestDist = dist;
                    secondClosestFace = face;
                }
            }

            if (closestFace == null || secondClosestFace == null) return (null, null);

            var candidates = new[]
            {
                closestFace.LeftNode.transform, closestFace.RightNode.transform,
                secondClosestFace.LeftNode.transform, secondClosestFace.RightNode.transform
            };

            Transform best = null;
            Transform secondBest = null;
            var bestDist = float.MaxValue;
            var secondBestDist = float.MaxValue;

            foreach (var candidate in candidates)
            {
                var dist = Vector3.Distance(transform.position, candidate.position);
                if (dist < bestDist)
                {
                    secondBestDist = bestDist;
                    secondBest = best;
                    bestDist = dist;
                    best = candidate;
                }
                else if (dist < secondBestDist)
                {
                    secondBestDist = dist;
                    secondBest = candidate;
                }
            }

            return (best, secondBest);
        }

        private bool CameraFacesNode(AlignNode node)
        {
            var camera = _stuyBase.GetActiveCamera();
            if (camera == null) return false;
            return Vector3.Dot(camera.transform.forward, node.transform.forward) > 0;
        }

        private bool IsFacingReef()
        {
            return _stuyBase.GetFacingReef();
        }

        // ---- Shared PID drive ----

        private void DriveManualPid(Vector3 targetPosition, float targetYawDegrees)
        {
            var dt = Time.fixedDeltaTime;

            var errorX = targetPosition.x - transform.position.x;
            var errorZ = targetPosition.z - transform.position.z;

            var outputX = translateX.Update(errorX, dt);
            var outputZ = translateZ.Update(errorZ, dt);

            var translateOutput = new Vector2(outputX, outputZ);
            if (translateOutput.magnitude > maxTranslateOutput)
            {
                translateOutput = translateOutput.normalized * maxTranslateOutput;
            }

            var currentYaw = ToMathYaw(transform.eulerAngles.y);
            var targetYaw = ToMathYaw(targetYawDegrees);
            var angleError = Mathf.Repeat(targetYaw - currentYaw + Mathf.PI, 2f * Mathf.PI) - Mathf.PI;
            var rotateOutput = rotate.Update(angleError, dt);

            _driveController.overideInput(translateOutput, rotateOutput, DriveController.DriveMode.FieldOriented);
        }

        private void ResetPid()
        {
            translateX.Reset();
            translateZ.Reset();
            rotate.Reset();
        }

        // Matches the yaw convention used by 340's proven GRRAutoAlign: converts Unity's left-handed Y euler
        // (0 = +Z, clockwise-positive) into a standard math angle (0 = +X, counter-clockwise-positive) so the
        // wrapped angle error can be computed the same well-tested way.
        private static float ToMathYaw(float unityYawDegrees)
        {
            return -Mathf.Deg2Rad * (unityYawDegrees - 90f);
        }

        /// <summary>True while this component is actively driving the robot toward a human player station.</summary>
        public bool StationAlignActive() => _stationAlignActive;

        /// <summary>True while this component is actively driving the robot toward the barge.</summary>
        public bool BargeAlignActive() => _bargeAlignActive;

        /// <summary>True while this component is actively driving the robot toward a reef branch.</summary>
        public bool ReefAlignActive() => _reefAlignActive;

        /// <summary>True if the reef branch currently being targeted is the left one.</summary>
        public bool ReefAlignLeft() => _reefAlignLeft;
    }
}
