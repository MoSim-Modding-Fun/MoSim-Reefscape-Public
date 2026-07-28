using System.Collections;
using System.Collections.Generic;
using Games.Reefscape.Enums;
using Games.Reefscape.GamePieceSystem;
using Games.Reefscape.FieldScripts;
using MoSimCore.BaseClasses.GameManagement;
using MoSimCore.Enums;
using RobotFramework.Controllers.GamePieceSystem;
using UnityEngine;

namespace Prefabs.Reefscape.Robots.Mods.NYModpack._694
{
    public class LedStripController : MonoBehaviour
    {
        public GameObject[] leds;
        private Material LEDs;
        public Shader shaderGraphShader;
        public Texture hasAlgae;
        public Texture Disabled;
        public Texture Intake;
        public Texture hasCoral;
        public Texture CoralMode;
        public Texture AlgaeMode;
        public Texture L1Mode;
        public Texture prepClimb;
        public Texture Climbed;
        public Texture AutoAligning;
        public Texture AutoAligned;
        public Texture Processor;
        public Texture off;

        [Header("Alignment Detection")]
        [Tooltip("Position tolerance in meters")]
        public float positionTolerance = 0.05f;
        [Tooltip("Rotation tolerance in degrees")]
        public float rotationTolerance = 5f;

        protected RobotGamePieceControllerBase GamePieceManager { get; private set; }
        protected Games.Reefscape.Robots.ReefscapeRobotBase ReefscapeRobotBase { get; private set; }
        
        private RobotGamePieceController<ReefscapeGamePiece, ReefscapeGamePieceData>.GamePieceControllerNode _coralController;
        private RobotGamePieceController<ReefscapeGamePiece, ReefscapeGamePieceData>.GamePieceControllerNode _algaeController;

        private StuyPulseOffseason OPRobotics;
        private ReefscapeAutoAlign autoAlign;
        
        private bool hasTriggeredAlgaeIntake = false;
        private int previousAlgaeCount = 0;

        // Alignment tracking
        private List<AlignNode> targetNodes = new List<AlignNode>();
        private Dictionary<Transform, AlignNode> parentLookup = new Dictionary<Transform, AlignNode>();
        private bool alignmentInitialized = false;
        
        private void Start()
        {
            GamePieceManager = GetComponent<RobotGamePieceControllerBase>();
            ReefscapeRobotBase = GetComponent<Games.Reefscape.Robots.ReefscapeRobotBase>();
            OPRobotics = GetComponent<StuyPulseOffseason>();
            autoAlign = GetComponent<ReefscapeAutoAlign>();

            // Get the coral and algae controllers from OPRobotics
            if (OPRobotics != null)
            {
                _coralController = OPRobotics._coralController;
                _algaeController = OPRobotics._algaeController;
            }

            LEDs = new Material(shaderGraphShader);
            
            foreach (var led in leds)
            {
                led.GetComponent<Renderer>().material = LEDs;
            }

            InitializeAlignmentNodes();
        }

        private void InitializeAlignmentNodes()
        {
            var nodes = GameObject.FindGameObjectsWithTag("ReefFace");
        
            foreach (var node in nodes)
            {
                if (node.TryGetComponent<AlignNode>(out var tar))
                {
                    targetNodes.Add(tar);
                }
            }

            foreach (var node in targetNodes)
            {
                if (node.LeftNode != null)
                    parentLookup.TryAdd(node.LeftNode.transform, node);
                if (node.RightNode != null)
                    parentLookup.TryAdd(node.RightNode.transform, node);
            }

            alignmentInitialized = true;
        }

        private bool IsAligned
        {
            get
            {
                if (!alignmentInitialized || autoAlign == null) return false;
                if (!ReefscapeRobotBase.AutoAlignLeftAction.IsPressed() && 
                    !ReefscapeRobotBase.AutoAlignRightAction.IsPressed())
                {
                    return false;
                }

                var targetNode = GetCurrentAlignmentTarget();
                if (targetNode == null) return false;

                return CheckIfAlignedToNode(targetNode);
            }
        }

        private Transform GetCurrentAlignmentTarget()
        {
            if (targetNodes.Count == 0) return null;

            // Find closest faces
            AlignNode closest = null;
            AlignNode secondClosest = null;
            float closestDist = float.MaxValue;
            float secondClosestDist = float.MaxValue;

            foreach (var node in targetNodes)
            {
                if (node == null || node.transform == null) continue;
                
                float dist = Vector3.Distance(transform.position, node.transform.position);

                if (dist < closestDist)
                {
                    secondClosestDist = closestDist;
                    secondClosest = closest;
                    closestDist = dist;
                    closest = node;
                }
                else if (dist < secondClosestDist)
                {
                    secondClosestDist = dist;
                    secondClosest = node;
                }
            }

            if (closest == null) return null;

            // Get closest points from the two closest faces
            var candidates = new List<(Transform transform, float distance)>();
            
            if (closest.LeftNode != null)
                candidates.Add((closest.LeftNode.transform, Vector3.Distance(transform.position, closest.LeftNode.transform.position)));
            if (closest.RightNode != null)
                candidates.Add((closest.RightNode.transform, Vector3.Distance(transform.position, closest.RightNode.transform.position)));
            
            if (secondClosest != null)
            {
                if (secondClosest.LeftNode != null)
                    candidates.Add((secondClosest.LeftNode.transform, Vector3.Distance(transform.position, secondClosest.LeftNode.transform.position)));
                if (secondClosest.RightNode != null)
                    candidates.Add((secondClosest.RightNode.transform, Vector3.Distance(transform.position, secondClosest.RightNode.transform.position)));
            }

            // Find the closest candidate
            Transform closestPoint = null;
            float minDist = float.MaxValue;

            foreach (var (t, d) in candidates)
            {
                if (d < minDist)
                {
                    minDist = d;
                    closestPoint = t;
                }
            }

            if (closestPoint == null) return null;

            // Check if this is the correct side based on button press
            if (!parentLookup.TryGetValue(closestPoint, out var parentNode)) return null;

            bool isLeftButton = ReefscapeRobotBase.AutoAlignLeftAction.IsPressed();
            bool isLeftNode = parentNode.LeftNode != null && parentNode.LeftNode.transform == closestPoint;

            // Determine based on perspective mode
            bool usePerspective = PlayerPrefs.GetInt("PerspectiveAutoAlign", 1) == 1;
            
            if (usePerspective)
            {
                // In perspective mode, check camera facing
                GameObject activeCamera = ReefscapeRobotBase.GetActiveCamera();
                if (activeCamera != null)
                {
                    Vector3 cameraForward = activeCamera.transform.forward;
                    Vector3 nodeForward = parentNode.transform.forward;
                    float dotProduct = Vector3.Dot(cameraForward, nodeForward);
                    bool cameraFacesNode = dotProduct > 0;

                    // Left button with camera NOT facing = left node
                    // Right button with camera facing = right node
                    bool shouldBeLeftNode = isLeftButton ? !cameraFacesNode : cameraFacesNode;
                    
                    if (isLeftNode == shouldBeLeftNode)
                        return closestPoint;
                }
            }
            else
            {
                // Reef relative mode - simple left/right matching
                if ((isLeftButton && isLeftNode) || (!isLeftButton && !isLeftNode))
                    return closestPoint;
            }

            return null;
        }

        private bool CheckIfAlignedToNode(Transform targetNode)
        {
            if (targetNode == null || autoAlign == null) return false;

            // Calculate target position with offset
            Vector3 targetPosition = targetNode.position + (targetNode.rotation * (autoAlign.offset * 0.0254f));
            
            // Check position error
            float positionError = Vector3.Distance(transform.position, targetPosition);
            if (positionError > positionTolerance) return false;

            // Calculate target rotation
            Quaternion targetRotation = targetNode.rotation;
            
            bool isFacingReef = ReefscapeRobotBase.GetFacingReef();
            if (!isFacingReef && autoAlign.enableBackwardsAlign)
            {
                targetRotation *= Quaternion.Euler(0, 180, 0);
            }
            
            targetRotation *= Quaternion.Euler(0, autoAlign.rotation, 0);

            // Check rotation error
            float rotationError = Quaternion.Angle(transform.rotation, targetRotation);
            if (rotationError > rotationTolerance) return false;

            return true;
        }

        private void Update()
        {
            bool hasAlgaePiece = _algaeController != null && _algaeController.HasPiece();
            bool hasCoralPiece = _coralController != null && _coralController.HasPiece();

            if (previousAlgaeCount > 0 && !hasAlgaePiece)
            {
                hasTriggeredAlgaeIntake = false;
            }

            previousAlgaeCount = hasAlgaePiece ? 1 : 0;

            if ((ReefscapeRobotBase is null || GamePieceManager is null) || BaseGameManager.Instance.RobotState == RobotState.Disabled)
            {
                LEDs.SetFloat("_X", 0);
                LEDs.SetFloat("_Y", 0.5f);
                LEDs.SetFloat("_intensity", 20);
                LEDs.SetTexture("_Texture2D", Disabled);
            }
            else if (ReefscapeRobotBase.CurrentSetpoint == ReefscapeSetpoints.Climb)
            {
                LEDs.SetFloat("_X", 0);
                LEDs.SetFloat("_Y", 0f);
                LEDs.SetFloat("_intensity", 20);
                LEDs.SetTexture("_Texture2D", prepClimb);
            }
            else if (ReefscapeRobotBase.CurrentSetpoint == ReefscapeSetpoints.Climbed)
            {
                LEDs.SetFloat("_X", 3f);
                LEDs.SetFloat("_Y", 0.0f);
                LEDs.SetFloat("_intensity", 20);
                LEDs.SetTexture("_Texture2D", Climbed);
            }
            else if (ReefscapeRobotBase.AutoAlignLeftAction.IsPressed() || ReefscapeRobotBase.AutoAlignRightAction.IsPressed())
            {
                if (IsAligned)
                {
                    LEDs.SetFloat("_X", 0);
                    LEDs.SetFloat("_Y", 0.0f);
                    LEDs.SetFloat("_intensity", 20);
                    LEDs.SetTexture("_Texture2D", AutoAligned);
                }
                else
                {
                    LEDs.SetFloat("_X", 0);
                    LEDs.SetFloat("_Y", 0.0f);
                    LEDs.SetFloat("_intensity", 20);
                    LEDs.SetTexture("_Texture2D", AutoAligning);
                }
            }
            else if (GamePieceManager != null && hasAlgaePiece && ReefscapeRobotBase.IntakeAction.IsPressed())
            {
                if (!hasTriggeredAlgaeIntake)
                {
                    hasTriggeredAlgaeIntake = true;
                }
                
                LEDs.SetFloat("_X", 0f);
                LEDs.SetFloat("_Y", 0f);
                LEDs.SetFloat("_intensity", 40);
                LEDs.SetTexture("_Texture2D", hasAlgae);
            }
            else if (GamePieceManager != null && OPRobotics._coralController.HasPiece() && 
                     OPRobotics._coralController.currentStateNum == 6) // coralArmStowState is index 6 in the states array
            {
                LEDs.SetFloat("_X", 1.5f);
                LEDs.SetFloat("_Y", 0.0f);
                LEDs.SetFloat("_intensity", 20);
                LEDs.SetTexture("_Texture2D", hasCoral);
            }
            else
            {
                LEDs.SetFloat("_X", 0);
                LEDs.SetFloat("_Y", 0.5f);
                LEDs.SetFloat("_intensity", 20);
                LEDs.SetTexture("_Texture2D", off);
            }
        }
    }
}