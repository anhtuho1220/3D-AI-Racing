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
    private float closeDistance = 5f; 

    // Cached ray indices to avoid finding them every frame
    private int leftRayIndex = -1;
    private int rightRayIndex = -1;
    private bool rayIndicesCached = false;

    public override void Initialize()
    {
        carController = GetComponent<PrometeoAIController>();
        carRigidbody = GetComponent<Rigidbody>();
        raySensorComponent = GetComponent<RayPerceptionSensorComponent3D>();
        trackCheckpoints = FindFirstObjectByType<TrackCheckpoints>();
        
        startPosition = transform.position;
        startRotation = transform.rotation;
    }

    void Start()
    {
        if (trackCheckpoints != null)
        {
            trackCheckpoints.OnCarCorrectCheckpoint += OnCorrectCheckpoint;
            trackCheckpoints.OnCarWrongCheckpoint += OnWrongCheckpoint;
        }
    }

    public override void OnEpisodeBegin()
    {
        transform.position = startPosition;
        transform.rotation = startRotation;
        
        wrongCheckpointCount = 0;

        if (trackCheckpoints != null) trackCheckpoints.ResetCar(transform);
        carController.ResetCar();
    }

    public override void CollectObservations(VectorSensor sensor)
    {
        Vector3 checkpointForward = Vector3.zero;
        float distanceToCheckpoint = 0f;

        if (trackCheckpoints != null)
        {
            var nextCheckpoint = trackCheckpoints.GetNextCheckpoint(transform);
            if (nextCheckpoint != null)
            {
                checkpointForward = nextCheckpoint.transform.forward;
                distanceToCheckpoint = Vector3.Distance(transform.position, nextCheckpoint.transform.position);
            }
        }

        Vector3 localVelocity = Vector3.zero;
        if (carRigidbody != null)
        {
            localVelocity = transform.InverseTransformDirection(carRigidbody.linearVelocity);
        }

        float directionDot = Vector3.Dot(transform.forward, checkpointForward);
        
        sensor.AddObservation(directionDot);
        //sensor.AddObservation(localVelocity);
        
        // Divide by arbitrarily expected max distance (100) to keep Neural Network gradients stable
        //sensor.AddObservation(distanceToCheckpoint / 100f);
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

        if (carRigidbody != null)
        {
            ProcessMovementRewards();
            ProcessCheckpointAlignmentRewards();
        }

        if (raySensorComponent != null && raySensorComponent.RaySensor != null)
        {
            ProcessRaycastRewards();
        }
    }

    private void ProcessMovementRewards()
    {
        // Re-calculate the specific forward speed per frame!
        float currentForwardSpeed = Vector3.Dot(carRigidbody.linearVelocity, transform.forward);
        
        if (currentForwardSpeed > 0.05f)
        {
            AddReward(currentForwardSpeed * 0.02f);
        }
        else if (currentForwardSpeed < -1f)
        {
            AddReward(-0.01f);
        }
        else
        {
            AddReward(-0.05f); // Penalize for standing still
        }
    }

    private void ProcessCheckpointAlignmentRewards()
    {
        if (trackCheckpoints == null) return;
        
        var nextCheckpoint = trackCheckpoints.GetNextCheckpoint(transform);
        if (nextCheckpoint != null)
        {
            float directionDot = Vector3.Dot(transform.forward, nextCheckpoint.transform.forward);
            if (directionDot < 0f)
            {
                AddReward(-0.02f);
            }

            Vector3 directionToCheckpoint = (nextCheckpoint.transform.position - transform.position).normalized;
            float velocityAlignment = Vector3.Dot(transform.forward, directionToCheckpoint);

            if (velocityAlignment > 0f) 
            {
                AddReward(velocityAlignment * 0.01f); 
            }
        }
    }

    private void ProcessRaycastRewards()
    {
        var rayOutput = raySensorComponent.RaySensor.RayPerceptionOutput;
        var rayInput = raySensorComponent.GetRayPerceptionInput();

        if (rayOutput.RayOutputs == null || rayOutput.RayOutputs.Length == 0 || rayInput.Angles.Count == 0) return;

        int totalRays = rayOutput.RayOutputs.Length;
        
        // Cache min/max angles to find the outermost rays (leftmost/rightmost) just once
        if (!rayIndicesCached) 
        {
             float maxAngle = float.MinValue;
             float minAngle = float.MaxValue;
             for (int i = 0; i < rayInput.Angles.Count; i++)
             {
                 if (rayInput.Angles[i] > maxAngle)
                 {
                     maxAngle = rayInput.Angles[i];
                     leftRayIndex = i;
                 }
                 if (rayInput.Angles[i] < minAngle)
                 {
                     minAngle = rayInput.Angles[i];
                     rightRayIndex = i;
                 }
             }
             rayIndicesCached = true;
        }

        int closeRayCount = 0;
        int activeRayCount = 0;

        for (int i = 0; i < totalRays; i++)
        {
            var ray = rayOutput.RayOutputs[i];
            
            if (ray.HitTaggedObject)
            {
                string hitTag = rayInput.DetectableTags[ray.HitTagIndex];
                if (hitTag == "Left Walls" || hitTag == "Right Walls")
                {
                    activeRayCount++;
                    float hitDistance = ray.HitFraction * rayInput.RayLength;
                    if (hitDistance < closeDistance)
                    {
                        closeRayCount++;
                    }
                }
            }
        }

        // Penalize if >50% of active rays are too close to walls
        if (activeRayCount > 0 && (float)closeRayCount / activeRayCount > 0.5f)
        {
            AddReward(-0.05f);
        }

        // Reward for correct spatial positioning using outermost rays
        if (leftRayIndex >= 0 && rightRayIndex >= 0)
        {
            var leftRay = rayOutput.RayOutputs[leftRayIndex];
            var rightRay = rayOutput.RayOutputs[rightRayIndex];

            if (leftRay.HitTaggedObject && rightRay.HitTaggedObject)
            {
                if (rayInput.DetectableTags[leftRay.HitTagIndex] == "Left Walls" && 
                    rayInput.DetectableTags[rightRay.HitTagIndex] == "Right Walls")
                {
                    AddReward(0.01f);
                }
                else {
                    AddReward(-1f);
                }
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

    private void OnCollisionEnter(Collision collision)
    {
        if (collision.gameObject.TryGetComponent<Wall>(out _) || collision.gameObject.TryGetComponent<CarDriverAgent>(out _))
        {
            AddReward(-0.5f);
        }
    }

    private void OnCollisionStay(Collision collision)
    {
        if (collision.gameObject.TryGetComponent<Wall>(out _) || collision.gameObject.TryGetComponent<CarDriverAgent>(out _))
        {
            AddReward(-0.1f);
        }
    }

    private void OnCorrectCheckpoint(object sender, TrackCheckpoints.CarCheckpointEventArgs e)
    {
        if (e.carTransform == transform) 
        {
            float directionalBonus = 0f;
            var crossedCheckpoint = trackCheckpoints.GetNextCheckpoint(transform);
            
            if (crossedCheckpoint != null)
            {
                float alignment = Vector3.Dot(transform.forward, crossedCheckpoint.transform.forward);
                if (alignment > 0f)
                {
                    directionalBonus = alignment; // up to +1f bonus if perfectly aligned
                }
            }
            AddReward(1f + directionalBonus);
            wrongCheckpointCount = 0;
        }
    }

    private void OnWrongCheckpoint(object sender, TrackCheckpoints.CarCheckpointEventArgs e)
    {
        if (e.carTransform == transform) 
        {
            wrongCheckpointCount++;
            AddReward(-10f);

            if (wrongCheckpointCount >= 3)
            {
                AddReward(-100.0f);
                EndEpisode();
            }
        }
    }
}
