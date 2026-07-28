using System.Collections.Generic;
using Games.Reefscape.Enums;
using Games.Reefscape.FieldScripts;
using Games.Reefscape.Robots;
using Games.Reefscape.Scoring.Scorers;
using MoSimCore.Enums;
using RobotFramework.Controllers.Drivetrain;
using UnityEngine;

namespace Prefabs.Reefscape.Robots.Mods.NYModpack._694
{
    /// <summary>
    /// Single auto-align component for the offseason robot, replacing both ReefscapeAutoAlign and
    /// OffseasonBargeAutoAlign. Handles reef branch alignment (coral/algae, same logic as
    /// ReefscapeAutoAlign) and barge scoring alignment (closest BargeScorer with a corner-to-corner
    /// slider, same logic as StuyPulseAutoAlign.TryGetZoneTarget). Having one component avoids two
    /// AutoAlign subclasses fighting over the shared base-class PID state on the same GameObject.
    ///
    /// Barge align activates when CurrentSetpoint == Barge and either align button is held; all other
    /// setpoints fall through to reef branch align, identical to the original ReefscapeAutoAlign.
    /// StuyPulseOffseason sets `offset` each frame for reef branches and `bargeOffset` each frame for
    /// the barge case.
    /// </summary>
    public class OffseasonAutoAlign : AutoAlign
    {
        [Header("Reef Branch Align")]
        public Vector3 offset;
        public float rotation;
        [Tooltip("Enable forward auto align (when facing the reef)")]
        public bool enableForwardAlign = true;
        [Tooltip("Enable backwards auto align (when facing away from the reef)")]
        public bool enableBackwardsAlign = true;
        [Tooltip("Maximum distance from alignment node for auto align to activate (in feet)")]
        [SerializeField] private float maxAlignDistanceFeet = 15f;

        [Header("Barge Align")]
        public Vector3 bargeOffset;
        public float bargeRotation;
        [Tooltip("Standoff distance (inches) from barge center to slide line on the front side (sideA, +right).")]
        [SerializeField] private float bargeFrontStandoffInches = 118f;
        [Tooltip("Standoff distance (inches) from barge center to slide line on the back side (sideB, -right).")]
        [SerializeField] private float bargeBackStandoffInches = 118f;
        [Tooltip("Only engage barge align within this distance (feet) of the slide-line center.")]
        [SerializeField] private float maxBargeAlignDistanceFeet = 20f;
        [Tooltip("Half the length (inches) of the slide line along the barge's forward axis.")]
        [SerializeField] private float bargeHalfWidthInches = 40f;
        [Tooltip("How fast (world units/sec at full stick deflection) the slide target moves along the barge line.")]
        [SerializeField] private float bargeSlideSpeed = 2.5f;

        [Header("Reef Avoidance")]
        [Tooltip("Minimum clearance radius (meters) to maintain around the reef center when routing to the barge.")]
        [SerializeField] private float reefAvoidRadius = 2.5f;
        [Tooltip("Once routing around the reef, require the straight path to clear by this multiple of reefAvoidRadius before switching back to a direct line.")]
        [SerializeField] private float reefAvoidExitMargin = 1.3f;

        private const float FEET_TO_METERS = 0.3048f;
        private const float REEF_AVOID_LEAD_ANGLE_DEGREES = 35f;
        private const float REEF_AVOID_SAME_SIDE_ANGLE_DEGREES = 90f;
        private const float INCHES_TO_METERS = 0.0254f;
        private const float MIN_LINE_LENGTH = 0.01f;

        // Reef branch state
        private Vector3 _realOffset;
        private readonly List<AlignNode> _targetNodes = new();
        private readonly Dictionary<Transform, AlignNode> _parentLookup = new();
        private AlignNode _closest;
        private AlignNode _secondClosest;
        private (Transform, float)[] _candidates;
        private bool _startup;
        private Transform _closests;
        private Transform _secondCloses;

        // Barge state
        private readonly List<BargeScorer> _bargeScorers = new();
        private float _bargeSlide;
        private float _bargeSlideBaseline;
        private bool _bargeEngaged;
        private bool _bargeRoutingAroundReef;
        private float _bargeRoutingSide;

        // Reef avoidance state
        private Vector3 _blueReefPos;
        private Vector3 _redReefPos;
        private bool _hasReefPos;

        private ReefscapeRobotBase _base;

        private void Awake()
        {
            _startup = true;
            _base = GetComponent<ReefscapeRobotBase>();
        }

        private void Update()
        {
            if (_base == null) return;

            if (_base.AutoAlignLeftAction.triggered || _base.AutoAlignRightAction.triggered)
            {
                ClosestFaces();
                (_closests, _secondCloses) = ClosestPoints();
            }

            _realOffset = offset * INCHES_TO_METERS;
        }

        private void FixedUpdate()
        {
            if (_startup)
            {
                foreach (var node in GameObject.FindGameObjectsWithTag("ReefFace"))
                {
                    if (node.TryGetComponent<AlignNode>(out var tar))
                        _targetNodes.Add(tar);
                }
                foreach (var node in _targetNodes)
                {
                    _parentLookup.TryAdd(node.LeftNode.transform, node);
                    _parentLookup.TryAdd(node.RightNode.transform, node);
                }
                _candidates = new (Transform, float)[4];

                foreach (var found in FindObjectsByType(typeof(BargeScorer), FindObjectsSortMode.None))
                {
                    if (found is BargeScorer scorer) _bargeScorers.Add(scorer);
                }

                var blueReef = GameObject.Find("BlueReef");
                var redReef = GameObject.Find("RedReef");
                if (blueReef != null && redReef != null)
                {
                    _blueReefPos = blueReef.transform.position;
                    _redReefPos = redReef.transform.position;
                    _hasReefPos = true;
                }

                _startup = false;
            }

            if (_base == null) return;

            if (_base.CurrentSetpoint == ReefscapeSetpoints.Barge)
            {
                TryBargeAlign();
                return;
            }

            _bargeEngaged = false;

            if (_base.CurrentSetpoint == ReefscapeSetpoints.Place) return;

            if (PlayerPrefs.GetInt("PerspectiveAutoAlign", 1) == 1)
                PerspectiveRelativeAlign();
            else
                ReefRelativeAlign();
        }

        // ---- Barge align ----

        private void TryBargeAlign()
        {
            if (!(_base.AutoAlignLeftAction.IsPressed() || _base.AutoAlignRightAction.IsPressed()))
            {
                _bargeEngaged = false;
                _bargeRoutingAroundReef = false;
                _bargeRoutingSide = 0f;
                return;
            }

            var closest = GetClosestBargeScorer();
            if (closest == null) { _bargeEngaged = false; _bargeRoutingAroundReef = false; _bargeRoutingSide = 0f; return; }

            var reference = closest.transform;
            var halfWidth = bargeHalfWidthInches * INCHES_TO_METERS;

            // Determine which side of the barge the robot is on.
            var useSideA = Vector3.Dot(transform.position - reference.position, reference.right) > 0f;
            var faceDirection = useSideA ? -reference.right : reference.right;
            var sideSign = useSideA ? 1f : -1f;

            // Standoff based on whether the robot approaches front- or back-first (robot's current orientation).
            var approachFront = Vector3.Dot(transform.forward, faceDirection) >= 0f;
            var standoff = (approachFront ? bargeFrontStandoffInches : bargeBackStandoffInches) * INCHES_TO_METERS;

            var center = reference.position
                         + reference.right * (sideSign * standoff)
                         + reference.right * (sideSign * bargeOffset.x * INCHES_TO_METERS)
                         + Vector3.up * (bargeOffset.y * INCHES_TO_METERS)
                         + reference.forward * (bargeOffset.z * INCHES_TO_METERS);

            // Max distance check against the chosen side's center.
            var robotXZ = new Vector2(transform.position.x, transform.position.z);
            var centerXZ = new Vector2(center.x, center.z);
            if (Vector2.Distance(robotXZ, centerXZ) > maxBargeAlignDistanceFeet * FEET_TO_METERS)
            {
                _bargeEngaged = false;
                _bargeRoutingAroundReef = false;
                _bargeRoutingSide = 0f;
                return;
            }

            var leftCorner = center - reference.forward * halfWidth;
            var rightCorner = center + reference.forward * halfWidth;
            var lineVector = rightCorner - leftCorner;
            var lineLength = lineVector.magnitude;
            if (lineLength < MIN_LINE_LENGTH) { _bargeEngaged = false; _bargeRoutingAroundReef = false; _bargeRoutingSide = 0f; return; }

            var closestT = Vector3.Dot(transform.position - leftCorner, lineVector) / (lineLength * lineLength);

            if (!_bargeEngaged) _bargeSlide = 0f;
            _bargeEngaged = true;

            if (_bargeSlide == 0f) _bargeSlideBaseline = closestT;

            var stick = ApplyCameraFlip(_base.TranslateAction.ReadValue<Vector2>().x, lineVector);
            _bargeSlide += stick * bargeSlideSpeed * Time.fixedDeltaTime / lineLength;

            var finalT = Mathf.Clamp01(_bargeSlideBaseline + _bargeSlide);
            var finalTarget = Vector3.Lerp(leftCorner, rightCorner, finalT);

            // Pick whichever of the two perpendicular barge headings is closest to the robot's current yaw.
            var yawA = Quaternion.LookRotation(reference.right, Vector3.up).eulerAngles.y;
            var yawB = Quaternion.LookRotation(-reference.right, Vector3.up).eulerAngles.y;
            var robotYaw = transform.eulerAngles.y;
            var targetYaw = Mathf.Abs(Mathf.DeltaAngle(robotYaw, yawA)) < Mathf.Abs(Mathf.DeltaAngle(robotYaw, yawB)) ? yawA : yawB;
            var targetRotation = Quaternion.Euler(0, targetYaw + bargeRotation, 0);

            finalTarget = ApplyReefAvoidance(finalTarget, ref _bargeRoutingAroundReef, ref _bargeRoutingSide, NearestReefPos(transform.position));

            AlignPosition(finalTarget, targetRotation);
        }

        private float ApplyCameraFlip(float stickValue, Vector3 lineDirection)
        {
            var camera = _base.GetActiveCamera();
            if (camera == null) return stickValue;
            var cameraRight = camera.transform.right;
            cameraRight.y = 0f;
            if (cameraRight.sqrMagnitude < 0.0001f) return stickValue;
            var flatLine = new Vector3(lineDirection.x, 0f, lineDirection.z);
            if (flatLine.sqrMagnitude < 0.0001f) return stickValue;
            return Vector3.Dot(cameraRight.normalized, flatLine.normalized) >= 0f ? stickValue : -stickValue;
        }

        private BargeScorer GetClosestBargeScorer()
        {
            BargeScorer closest = null;
            var closestDist = float.MaxValue;
            foreach (var scorer in _bargeScorers)
            {
                if (scorer == null || scorer.Alliance != _base.Alliance) continue;
                var dist = Vector3.Distance(transform.position, scorer.transform.position);
                if (dist < closestDist) { closestDist = dist; closest = scorer; }
            }
            return closest;
        }

        private Vector3 NearestReefPos(Vector3 pos)
        {
            if (!_hasReefPos) return pos;
            return Vector3.Distance(pos, _blueReefPos) <= Vector3.Distance(pos, _redReefPos) ? _blueReefPos : _redReefPos;
        }

        private Vector3 ApplyReefAvoidance(Vector3 realTarget, ref bool routingAroundReef, ref float routingSide, Vector3? reefPosOverride = null)
        {
            if (!_hasReefPos) return realTarget;
            var reefPos = reefPosOverride ?? (_base.Alliance == Alliance.Blue ? _blueReefPos : _redReefPos);
            return ApplyCircularAvoidance(realTarget, reefPos, reefAvoidRadius, reefAvoidExitMargin, REEF_AVOID_SAME_SIDE_ANGLE_DEGREES, REEF_AVOID_LEAD_ANGLE_DEGREES, ref routingAroundReef, ref routingSide);
        }

        private Vector3 ApplyCircularAvoidance(Vector3 realTarget, Vector3 obstaclePos, float avoidRadius, float exitMargin, float sameSideSkipAngleDeg, float leadAngleDegMax, ref bool routingAround, ref float routingSide)
        {
            var robotPos = transform.position;
            var toTarget = realTarget - robotPos;
            toTarget.y = 0f;
            var distanceToTarget = toTarget.magnitude;

            if (distanceToTarget < avoidRadius)
            {
                routingAround = false;
                routingSide = 0f;
                return realTarget;
            }

            var robotOffset = robotPos - obstaclePos;
            robotOffset.y = 0f;
            var targetOffset = realTarget - obstaclePos;
            targetOffset.y = 0f;
            var robotAngleDeg = Mathf.Atan2(robotOffset.z, robotOffset.x) * Mathf.Rad2Deg;
            var targetAngleDeg = Mathf.Atan2(targetOffset.z, targetOffset.x) * Mathf.Rad2Deg;
            var angularSeparationDeg = Mathf.Abs(Mathf.DeltaAngle(robotAngleDeg, targetAngleDeg));

            var targetToObstacle = obstaclePos - realTarget;
            targetToObstacle.y = 0f;
            if (targetToObstacle.magnitude < avoidRadius && angularSeparationDeg < sameSideSkipAngleDeg)
            {
                routingAround = false;
                routingSide = 0f;
                return realTarget;
            }

            var lineDir = toTarget / distanceToTarget;
            var toObstacle = obstaclePos - robotPos;
            toObstacle.y = 0f;
            var projection = Mathf.Clamp(Vector3.Dot(toObstacle, lineDir), 0f, distanceToTarget);
            var closestPointOnLine = robotPos + lineDir * projection;
            var obstacleClearance = Vector3.Distance(closestPointOnLine, obstaclePos);

            var exitThreshold = avoidRadius * exitMargin;
            var wasRouting = routingAround;
            var shouldRoute = wasRouting ? obstacleClearance < exitThreshold : obstacleClearance < avoidRadius;
            routingAround = shouldRoute;

            if (!shouldRoute)
            {
                routingSide = 0f;
                return realTarget;
            }

            var freshEngage = !wasRouting || routingSide == 0f;
            if (freshEngage)
            {
                var shortestArcDeg = Mathf.DeltaAngle(robotAngleDeg, targetAngleDeg);
                routingSide = shortestArcDeg >= 0f ? 1f : -1f;
            }

            var leadAngleDeg = Mathf.Min(leadAngleDegMax, angularSeparationDeg);
            var waypointAngleDeg = robotAngleDeg + routingSide * leadAngleDeg;
            var waypointAngleRad = waypointAngleDeg * Mathf.Deg2Rad;
            var waypoint = obstaclePos + new Vector3(Mathf.Cos(waypointAngleRad), 0f, Mathf.Sin(waypointAngleRad)) * avoidRadius;
            waypoint.y = robotPos.y;

            return waypoint;
        }

        // ---- Reef branch align (ReefscapeAutoAlign logic) ----

        private bool CameraFacesNode(AlignNode node)
        {
            if (node == null) return false;
            var activeCamera = _base.GetActiveCamera();
            return Vector3.Dot(activeCamera.transform.forward, node.transform.forward) > 0;
        }

        private void PerspectiveRelativeAlign()
        {
            if (_base == null) return;

            if (_base.AutoAlignLeftAction.IsPressed())
            {
                _parentLookup.TryGetValue(_closests, out var cl);
                if (cl == null) return;
                if (TryAlignToNode(_closests, !CameraFacesNode(cl))) return;
                _parentLookup.TryGetValue(_secondCloses, out var sc);
                if (TryAlignToNode(_secondCloses, !CameraFacesNode(sc))) return;
                if (TryAlignToNode(cl.LeftNode.transform, !CameraFacesNode(cl))) return;
                if (TryAlignToNode(cl.RightNode.transform, !CameraFacesNode(cl))) return;
            }

            if (_base.AutoAlignRightAction.IsPressed())
            {
                _parentLookup.TryGetValue(_closests, out var cl);
                if (cl == null) return;
                if (TryAlignToNode(_closests, CameraFacesNode(cl))) return;
                _parentLookup.TryGetValue(_secondCloses, out var sc);
                if (TryAlignToNode(_secondCloses, CameraFacesNode(sc))) return;
                if (TryAlignToNode(cl.LeftNode.transform, CameraFacesNode(cl))) return;
                if (TryAlignToNode(cl.RightNode.transform, CameraFacesNode(cl))) return;
            }
        }

        private void ReefRelativeAlign()
        {
            if (_base == null) return;

            if (_base.AutoAlignLeftAction.IsPressed())
            {
                TryAlignToNode(_closests, true);
                TryAlignToNode(_secondCloses, true);
            }

            if (_base.AutoAlignRightAction.IsPressed())
            {
                TryAlignToNode(_closests, false);
                TryAlignToNode(_secondCloses, false);
            }
        }

        private bool TryAlignToNode(Transform targetNode, bool isLeftSide)
        {
            if (targetNode == null) return false;
            if (!_parentLookup.TryGetValue(targetNode, out var parentNode)) return false;

            var isCorrectNode = isLeftSide
                ? parentNode.LeftNode.transform == targetNode
                : parentNode.RightNode == targetNode.gameObject;
            if (!isCorrectNode) return false;

            var isFacingReef = _base.GetFacingReef();

            if (Vector3.Distance(transform.position, targetNode.position) > maxAlignDistanceFeet * FEET_TO_METERS) return false;

            var target = targetNode.transform;
            var targetRotation = target.rotation;
            var finalTarget = target.position;

            if ((!isFacingReef && enableBackwardsAlign) || !enableForwardAlign)
                targetRotation *= Quaternion.Euler(0, 180, 0);

            finalTarget += target.rotation * _realOffset;
            targetRotation *= Quaternion.Euler(0, rotation, 0);

            AlignPosition(finalTarget, targetRotation);
            return true;
        }

        private (Transform close, Transform sec) ClosestPoints()
        {
            if (_closest == null || _secondClosest == null) return (null, null);

            var pointA = _closest.LeftNode.transform;
            var pointB = _closest.RightNode.transform;
            var pointC = _secondClosest.LeftNode.transform;
            var pointD = _secondClosest.RightNode.transform;

            var origin = transform.position;
            _candidates[0] = (pointA, Vector3.Distance(pointA.position, origin));
            _candidates[1] = (pointB, Vector3.Distance(pointB.position, origin));
            _candidates[2] = (pointC, Vector3.Distance(pointC.position, origin));
            _candidates[3] = (pointD, Vector3.Distance(pointD.position, origin));

            Transform finalClosest = null;
            var finalCloseDist = float.MaxValue;
            Transform finalSecondClosest = null;
            var finalSecondCloseDist = float.MaxValue;

            foreach (var (pt, dist) in _candidates)
            {
                if (dist < finalCloseDist)
                {
                    finalSecondClosest = finalClosest;
                    finalSecondCloseDist = finalCloseDist;
                    finalClosest = pt;
                    finalCloseDist = dist;
                }
                else if (dist < finalSecondCloseDist)
                {
                    finalSecondClosest = pt;
                    finalSecondCloseDist = dist;
                }
            }

            return (finalClosest, finalSecondClosest);
        }

        private void ClosestFaces()
        {
            float closestDist = float.MaxValue;
            float secondClosestDist = float.MaxValue;
            _closest = null;
            _secondClosest = null;

            foreach (var node in _targetNodes)
            {
                if (node == null || node.transform == null) continue;
                var d = Vector3.Distance(transform.position, node.transform.position);
                if (d < closestDist)
                {
                    secondClosestDist = closestDist;
                    _secondClosest = _closest;
                    closestDist = d;
                    _closest = node;
                }
                else if (d < secondClosestDist)
                {
                    secondClosestDist = d;
                    _secondClosest = node;
                }
            }
        }
    }
}
