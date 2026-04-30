using UnityEngine;
using Unity.MLAgents;
using Unity.MLAgents.Sensors;
using Unity.MLAgents.Actuators;
using UnityEngine.InputSystem;

public class CarDriverAgent : Agent
{
    private TrackCheckpoints trackCheckpoints;
    private Vector3 startPosition;
    private Quaternion startRotation;

    private PrometeoAIController carController;
    private Rigidbody carRigidbody;
    private RayPerceptionSensorComponent3D raySensorComponent;
    
    private int wrongCheckpointCount = 0;
    
    [Header("Reward Parameters")]
    [SerializeField] private float closeDistance = 5f; 
    [SerializeField] private float collisionPenalty = -1f;
    [SerializeField] private float collisionStayPenalty = -0.5f;
    [SerializeField] private float wrongCheckpointPenalty = -10f;
    [SerializeField] private float incorrectDirectionPenalty = -0.02f;
    [SerializeField] private float movingForwardReward = 0.01f;
    [SerializeField] private float standingStillPenalty = -1f;
    [SerializeField] private float wrongRayPenalty = -0.1f;
    [SerializeField] private float closeWallPenalty = -0.1f;
    [SerializeField] private float episodeEndPenalty = -100.0f;
    [SerializeField] private float checkpointReward = 1f;

    // Cached ray indices to avoid finding them every frame
    private int[] leftRayIndices;
    private int[] rightRayIndices;
    private int frontRayIndex = -1;

    // Cached tag indices to avoid string comparison every frame
    private int leftWallTagIndex = -1;
    private int rightWallTagIndex = -1;

    private float currentSpeed;
    private float currentTurning;

    private bool isCloseToObject = false;
    private bool facingLeftWall = false;
    private bool facingRightWall = false;
    private bool isWrongWay = false;
    private bool isColliding = false;
    private int collisionCount = 0;

    // Cached per-step to avoid redundant lookups
    private CheckpointSingle cachedNextCheckpoint;

    public override void Initialize()
    {
        MaxStep = 20000;

        carController = GetComponent<PrometeoAIController>();
        carRigidbody = GetComponent<Rigidbody>();
        
        foreach (var sensor in GetComponents<RayPerceptionSensorComponent3D>())
        {
            if (sensor.SensorName == "FrontSensor")
            {
                raySensorComponent = sensor;
                break;
            }
        }
        if (raySensorComponent == null) raySensorComponent = GetComponent<RayPerceptionSensorComponent3D>();

        trackCheckpoints = FindFirstObjectByType<TrackCheckpoints>();
        
        startPosition = transform.position;
        startRotation = transform.rotation;

        InitializeRayCache();
    }

    private void InitializeRayCache()
    {
        if (raySensorComponent == null) return;
        var rayInput = raySensorComponent.GetRayPerceptionInput();

        // Cache tag indices to replace per-frame string comparisons
        for (int i = 0; i < rayInput.DetectableTags.Count; i++)
        {
            if (rayInput.DetectableTags[i] == "Left Walls") leftWallTagIndex = i;
            else if (rayInput.DetectableTags[i] == "Right Walls") rightWallTagIndex = i;
        }

        for (int i = 0; i < rayInput.Angles.Count; i++)
        {
            if (Mathf.Approximately(rayInput.Angles[i], 90f))
            {
                frontRayIndex = i;
                break;
            }
        }
        var indexedAngles = new System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<int, float>>();
        for (int i = 0; i < rayInput.Angles.Count; i++)
        {
            indexedAngles.Add(new System.Collections.Generic.KeyValuePair<int, float>(i, rayInput.Angles[i]));
        }
        indexedAngles.Sort((a, b) => a.Value.CompareTo(b.Value));
        
        int count = Mathf.Min(3, indexedAngles.Count / 2);
        if (count == 0 && indexedAngles.Count > 0) count = 1;

        rightRayIndices = new int[count];
        leftRayIndices = new int[count];
        for (int i = 0; i < count; i++)
        {
            rightRayIndices[i] = indexedAngles[i].Key;
            leftRayIndices[i] = indexedAngles[indexedAngles.Count - 1 - i].Key;
        }
    }

    void Start()
    {
        trackCheckpoints.OnCarCorrectCheckpoint += OnCorrectCheckpoint;
        trackCheckpoints.OnCarWrongCheckpoint += OnWrongCheckpoint;
    }

    private void OnDestroy()
    {
        if (trackCheckpoints != null)
        {
            trackCheckpoints.OnCarCorrectCheckpoint -= OnCorrectCheckpoint;
            trackCheckpoints.OnCarWrongCheckpoint -= OnWrongCheckpoint;
        }
    }

    public override void OnEpisodeBegin()
    {
        transform.position = startPosition;
        transform.rotation = startRotation;
        
        wrongCheckpointCount = 0;
        isColliding = false;
        collisionCount = 0;

        trackCheckpoints.ResetCar(transform);
        carController.ResetCar();
    }

    public override void CollectObservations(VectorSensor sensor)
    {
        if (cachedNextCheckpoint == null)
            cachedNextCheckpoint = trackCheckpoints.GetNextCheckpoint(transform);

        Vector3 checkpointForward = cachedNextCheckpoint.transform.forward;
        float distanceToCheckpoint = Vector3.Distance(transform.position, cachedNextCheckpoint.transform.position);

        float directionDot = Vector3.Dot(transform.forward, checkpointForward);
        
        // Relative bearing to checkpoint (left/right + forward/back in local space)
        Vector3 localCheckpointDir = transform.InverseTransformPoint(cachedNextCheckpoint.transform.position);

        float lateralSpeed = transform.InverseTransformDirection(carRigidbody.linearVelocity).x;

        sensor.AddObservation(directionDot);                   // 1 float
        sensor.AddObservation(currentSpeed / 100f);            // 1 float — forward/back speed
        sensor.AddObservation(lateralSpeed / 100f);            // 1 float — lateral speed (drift detection)
        sensor.AddObservation(carRigidbody.angularVelocity.y); // 1 float — yaw rate
        sensor.AddObservation(distanceToCheckpoint);           // 1 float
        sensor.AddObservation(isColliding ? 1f : 0f);          // 1 float
        sensor.AddObservation(localCheckpointDir.x);           // 1 float — left/right bearing
        sensor.AddObservation(localCheckpointDir.z);           // 1 float — forward/back bearing
    }

    public override void OnActionReceived(ActionBuffers actions)
    {
        if (actions.ContinuousActions.Length < 2 || actions.DiscreteActions.Length < 1)
        {
            Debug.LogError("ML-Agents Behavior Parameters are not configured properly. Continuous Actions must be 2, and Discrete Branches must be 1.");
            return;
        }

        float throttle = actions.ContinuousActions[0];
        float steering = actions.ContinuousActions[1];
        bool brake = actions.DiscreteActions[0] == 1;
        
        carController.SetInputs(steering, throttle, brake);

        // Cache checkpoint once per decision step
        cachedNextCheckpoint = trackCheckpoints.GetNextCheckpoint(transform);

        ProcessRaycasts();
        ProcessMovementRewards();
        ProcessCheckpointAlignmentRewards();

        if (isColliding)
        {
            AddReward(collisionStayPenalty);
        }
    }

    private void ProcessMovementRewards()
    {
        currentSpeed = transform.InverseTransformDirection(carRigidbody.linearVelocity).z;
        currentTurning = transform.InverseTransformDirection(carRigidbody.angularVelocity).y;
        
        if (currentSpeed > 0.5f)
        {
            if (isWrongWay) { AddReward(-currentSpeed * 0.1f); }
            else { AddReward(currentSpeed * movingForwardReward + 0.01f); }
        }
        else if (currentSpeed < -0.5f)
        {
            if (isCloseToObject) { AddReward(0.05f); }
            else { AddReward(-0.05f); }
        }
        else
        {
            AddReward(standingStillPenalty); // Penalize for standing still
        }

        if (isWrongWay)
        {
            AddReward(-0.5f);
        }

        if (isCloseToObject)
        {
            if (currentSpeed < 0)
            {
                AddReward(0.01f);
                if (facingLeftWall && currentTurning > 0)
                {
                    AddReward(currentTurning * 0.1f);
                }
                else if (facingRightWall && currentTurning < 0)
                {
                    AddReward(currentTurning * -0.1f);
                }
            }
            else
            {
                AddReward(closeWallPenalty);
            }
        }
    }

    private void ProcessCheckpointAlignmentRewards()
    {
        float directionDot = Vector3.Dot(transform.forward, cachedNextCheckpoint.transform.forward);
        if (directionDot < 0f)
        {
            AddReward(incorrectDirectionPenalty);
        }

        Vector3 directionToCheckpoint = (cachedNextCheckpoint.transform.position - transform.position).normalized;
        float velocityAlignment = Vector3.Dot(transform.forward, directionToCheckpoint);

        if (velocityAlignment > 0f) 
        {
            AddReward(velocityAlignment * 0.1f);
        }
    }

    private void ProcessRaycasts()
    {
        isCloseToObject = false;
        facingLeftWall = false;
        facingRightWall = false;
        isWrongWay = false;

        var rayOutput = raySensorComponent.RaySensor.RayPerceptionOutput;
        var rayInput = raySensorComponent.GetRayPerceptionInput();

        if (rayOutput.RayOutputs == null || rayOutput.RayOutputs.Length == 0 || rayInput.Angles.Count == 0) return;

        int totalRays = rayOutput.RayOutputs.Length;

        int closeRayCount = 0;
        int activeRayCount = 0;

        bool wrongLeftRayHit = false;
        bool wrongRightRayHit = false;

        for (int i = 0; i < totalRays; i++)
        {
            var ray = rayOutput.RayOutputs[i];
            float hitDistance = ray.HitFraction * rayInput.RayLength;
            if (ray.HitTaggedObject)
            {
                if (hitDistance < closeDistance)
                {
                    activeRayCount++;
                    if (ray.HitTagIndex == leftWallTagIndex || ray.HitTagIndex == rightWallTagIndex)
                    {
                        closeRayCount++;
                    }
                }
            }
        }

        // Penalize if >40% of active rays are too close to walls
        if (activeRayCount > 0 && (float)closeRayCount / activeRayCount > 0.4f)
        {
            isCloseToObject = true;
        }

        if (frontRayIndex != -1)
        {
            var frontRay = rayOutput.RayOutputs[frontRayIndex];
            if (frontRay.HitTaggedObject)
            {
                if (frontRay.HitTagIndex == leftWallTagIndex)
                {
                    facingLeftWall = true;
                }
                else if (frontRay.HitTagIndex == rightWallTagIndex)
                {
                    facingRightWall = true;
                }
            }
        }

        // Reward for correct spatial positioning using outermost rays
        if (leftRayIndices != null && rightRayIndices != null)
        {
            for (int i = 0; i < leftRayIndices.Length; i++)
            {
                var leftRay = rayOutput.RayOutputs[leftRayIndices[i]];
                if (leftRay.HitTaggedObject)
                {
                    if (leftRay.HitTagIndex == leftWallTagIndex) { AddReward(0.01f); }
                    else if (leftRay.HitTagIndex == rightWallTagIndex)
                    {
                        wrongLeftRayHit = true;
                        AddReward(wrongRayPenalty);
                    }
                }
            }

            for (int i = 0; i < rightRayIndices.Length; i++)
            {
                var rightRay = rayOutput.RayOutputs[rightRayIndices[i]];
                if (rightRay.HitTaggedObject)
                {
                    if (rightRay.HitTagIndex == rightWallTagIndex) { AddReward(0.01f); }
                    else if (rightRay.HitTagIndex == leftWallTagIndex)
                    {
                        wrongRightRayHit = true;
                        AddReward(wrongRayPenalty);
                    }
                }
            }

            if (wrongLeftRayHit && wrongRightRayHit)
            {
                isWrongWay = true;
            }
        }
    }

    public override void Heuristic(in ActionBuffers actionsOut)
    {
        float throttle = 0f;
        float steering = 0f;
        int brake = 0;

        if (Keyboard.current != null)
        {
            if (Keyboard.current.wKey.isPressed) throttle = 1f;
            else if (Keyboard.current.sKey.isPressed) throttle = -1f;

            if (Keyboard.current.aKey.isPressed) steering = -1f;
            else if (Keyboard.current.dKey.isPressed) steering = 1f;

            if (Keyboard.current.spaceKey.isPressed) brake = 1;
        }
        
        var continuousActionsOut = actionsOut.ContinuousActions;
        var discreteActionsOut = actionsOut.DiscreteActions;

        if (continuousActionsOut.Length >= 2)
        {
            continuousActionsOut[0] = throttle;
            continuousActionsOut[1] = steering;
        }

        if (discreteActionsOut.Length >= 1)
        {
            discreteActionsOut[0] = brake;
        }
    }

    private bool IsRelevantCollision(Collision collision)
    {
        return collision.gameObject.TryGetComponent<Wall>(out _) || collision.gameObject.TryGetComponent<CarDriverAgent>(out _);
    }

    private void OnCollisionEnter(Collision collision)
    {
        if (IsRelevantCollision(collision))
        {
            collisionCount++;
            isColliding = true;
            AddReward(collisionPenalty);
        }
    }

    private void OnCollisionStay(Collision collision)
    {
        // Only maintain the flag — penalty is now applied at decision rate in OnActionReceived
        if (IsRelevantCollision(collision))
        {
            isColliding = true;
        }
    }

    private void OnCollisionExit(Collision collision)
    {
        if (IsRelevantCollision(collision))
        {
            collisionCount = Mathf.Max(0, collisionCount - 1);
            isColliding = collisionCount > 0;
        }
    }

    private void OnCorrectCheckpoint(object sender, TrackCheckpoints.CarCheckpointEventArgs e)
    {
        if (e.carTransform == transform) 
        {
            // Use cached checkpoint — this is the one we just crossed (cached before TrackCheckpoints advances the index)
            float directionalBonus = 0f;
            
            if (cachedNextCheckpoint != null)
            {
                float alignment = Vector3.Dot(transform.forward, cachedNextCheckpoint.transform.forward);
                if (alignment > 0f)
                {
                    directionalBonus = alignment; // up to +1f bonus if perfectly aligned
                }
            }
            
            AddReward(checkpointReward + directionalBonus);
            wrongCheckpointCount = 0;
        }
    }

    private void OnWrongCheckpoint(object sender, TrackCheckpoints.CarCheckpointEventArgs e)
    {
        if (e.carTransform == transform) 
        {
            wrongCheckpointCount++;
            AddReward(wrongCheckpointPenalty);

            if (wrongCheckpointCount >= 3)
            {
                AddReward(episodeEndPenalty);
                EndEpisode();
            }
        }
    }
}
