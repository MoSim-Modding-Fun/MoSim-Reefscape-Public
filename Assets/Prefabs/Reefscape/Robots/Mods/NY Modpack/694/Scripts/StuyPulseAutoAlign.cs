using System;
using System.Collections.Generic;
using Games.Reefscape.Enums;
using Games.Reefscape.FieldScripts;
using Games.Reefscape.GamePieceSystem;
using Games.Reefscape.Robots;
using MoSimCore.Enums;
using RobotFramework.Controllers.Drivetrain;
using RobotFramework.Controllers.GamePieceSystem;
using RobotFramework.GamePieceSystem;
using UnityEngine;

namespace Prefabs.Reefscape.Robots.Mods.NYPowerhousePack._694
{
    /// <summary>
    /// 694's single custom auto align - handles BOTH reef branch scoring and the human player station,
    /// the same way 340's GRRAutoAlign is one self-contained component rather than relying on the shared
    /// framework AutoAlign. It replaces the framework's ReefscapeAutoAlign component for this robot.
    ///
    /// Station align mirrors SwerveDrivePIDToCoralStation from the real 694 code as it existed at the
    /// Champs release (github.com/StuyPulse/Aunt-Mary/releases/tag/Champs) - one fixed pose per physical
    /// station, no left/right slot split (that was added later alongside NewArm on main). Station poses
    /// are plain serialized Vector3 positions so they can be placed by hand in the inspector.
    ///
    /// Reef branch align keeps the exact same node-finding and offset-application logic the framework's
    /// ReefscapeAutoAlign used (closest ReefFace-tagged AlignNode, perspective-relative left/right,
    /// the same AutoAlignOffset assets already tuned for this robot) so existing tuned values keep working -
    /// it's just re-hosted here so the whole thing runs through one PID loop instead of two components
    /// fighting over the drivetrain.
    ///
    /// Neither alignment mode uses RobotFramework.Controllers.PidSystems.PIDController (the shared
    /// controller the joints are tuned through) - the translation/rotation PID loops below are implemented
    /// from scratch so a future change to that shared PID controller cannot change this component's behavior.
    /// </summary>
    public class StuyPulseAutoAlign : MonoBehaviour
    {
        [Serializable]
        public class CoralStationTarget
        {
            public Alliance alliance;

            [Tooltip("World-space position (meters) of this station's pickup slot")]
            public Vector3 position;

            [Tooltip("Robot heading (degrees) to face when aligned to this station")]
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
        [Tooltip("One entry per physical coral station on the field, populated with its pickup slot position")]
        [SerializeField] private CoralStationTarget[] stationTargets;

        [Tooltip("Only assist toward the station within this distance (feet)")]
        [SerializeField] private float maxStationAlignDistanceFeet = 12f;

        [Header("Reef Branch Align")]
        [Tooltip("Game piece state that means coral is docked in the froggy/L1 holder - while true, always uses the L1 offset and never flips to backwards align")]
        [SerializeField] private GamePieceState froggyCoralStowState;

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

        private ReefscapeRobotBase _stuyBase;
        private DriveController _driveController;
        private RobotGamePieceController<ReefscapeGamePiece, ReefscapeGamePieceData> _pieces;

        private readonly List<AlignNode> _reefFaces = new();
        private readonly Dictionary<Transform, AlignNode> _reefNodeParents = new();
        private Transform _closestReefNode;
        private Transform _secondClosestReefNode;

        private bool _stationAlignActive;
        private bool _reefAlignActive;
        private bool _reefAlignLeft;

        private void Awake()
        {
            _stuyBase = GetComponent<ReefscapeRobotBase>();
            _driveController = GetComponent<DriveController>();
            _pieces = GetComponent<RobotGamePieceController<ReefscapeGamePiece, ReefscapeGamePieceData>>();
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
            var wasActive = _stationAlignActive || _reefAlignActive;

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

            if (_stuyBase == null || _driveController == null || stationTargets == null || stationTargets.Length == 0) return false;

            if (_stuyBase.AutoAlignLeftAction.IsPressed() || _stuyBase.AutoAlignRightAction.IsPressed()) return false;
            if (_stuyBase.CurrentRobotMode != ReefscapeRobotMode.Coral) return false;
            if (_stuyBase.CurrentIntakeMode != ReefscapeIntakeMode.Normal) return false;
            if (!_stuyBase.IntakeAction.IsPressed()) return false;

            var coral = _pieces != null ? _pieces.GetPieceByName(ReefscapeGamePieceType.Coral.ToString()) : null;
            if (coral != null && coral.HasPiece()) return false;

            var station = GetClosestStation(_stuyBase.Alliance);
            if (station == null) return false;

            var robotXZ = new Vector2(transform.position.x, transform.position.z);
            var stationXZ = new Vector2(station.position.x, station.position.z);
            if (Vector2.Distance(robotXZ, stationXZ) > maxStationAlignDistanceFeet * FEET_TO_METERS) return false;

            targetPosition = station.position;
            targetYaw = station.yRotation;
            return true;
        }

        private CoralStationTarget GetClosestStation(Alliance alliance)
        {
            CoralStationTarget closest = null;
            var closestDistance = float.MaxValue;
            var robotXZ = new Vector2(transform.position.x, transform.position.z);

            foreach (var station in stationTargets)
            {
                if (station == null || station.alliance != alliance) continue;

                var distance = Vector2.Distance(robotXZ, new Vector2(station.position.x, station.position.z));

                if (distance < closestDistance)
                {
                    closestDistance = distance;
                    closest = station;
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

            var coral = _pieces != null ? _pieces.GetPieceByName(ReefscapeGamePieceType.Coral.ToString()) : null;
            var holdingFroggyCoral = coral != null && froggyCoralStowState != null &&
                                      coral.currentStateNum == froggyCoralStowState.stateNum && coral.atTarget;

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

        /// <summary>True while this component is actively driving the robot toward a reef branch.</summary>
        public bool ReefAlignActive() => _reefAlignActive;

        /// <summary>True if the reef branch currently being targeted is the left one.</summary>
        public bool ReefAlignLeft() => _reefAlignLeft;
    }
}
