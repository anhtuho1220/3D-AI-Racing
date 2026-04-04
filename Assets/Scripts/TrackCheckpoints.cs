using System;
using UnityEngine;
using System.Collections.Generic;

public class TrackCheckpoints : MonoBehaviour
{
    public class CarCheckpointEventArgs : EventArgs
    {
        public Transform carTransform;
    }

    public event EventHandler<CarCheckpointEventArgs> OnCarCorrectCheckpoint;
    public event EventHandler<CarCheckpointEventArgs> OnCarWrongCheckpoint;

    [SerializeField] private List<Transform> carList;
    private List<CheckpointSingle> checkpointList;
    private List<int> nextCheckpointIndexList;

    private void Awake()
    {
        RefreshCheckpoints();
        nextCheckpointIndexList = new List<int>();
        foreach (Transform car in carList)
        {
            nextCheckpointIndexList.Add(0);
        }
    }

    public void RefreshCheckpoints()
    {
        Transform checkpointsContainer = transform.Find("Checkpoints");
        checkpointList = new List<CheckpointSingle>();
        if (checkpointsContainer == null)
        {
            Debug.LogError("Checkpoints container not found!");
            return;
        }

        foreach (Transform checkpoint in checkpointsContainer)
        {
            CheckpointSingle checkpointScript = checkpoint.GetComponentInChildren<CheckpointSingle>();
            
            if (checkpointScript == null)
            {
                Debug.LogError($"Checkpoint {checkpoint.name} is missing CheckpointSingle script!");
                continue;
            }

            checkpointScript.SetTrackCheckpoints(this);
            checkpointList.Add(checkpointScript);
        }
        nextCheckpointIndexList = new List<int>();
        foreach (Transform car in carList)
        {
            nextCheckpointIndexList.Add(0);
        }
    }

    public void CheckpointTriggered(CheckpointSingle checkpoint, Transform carTransform)
    {
        int carIndex = carList.IndexOf(carTransform);
        if (carIndex == -1) 
        {
            // Dynamically register randomly spawned cars
            carList.Add(carTransform);
            nextCheckpointIndexList.Add(0);
            carIndex = carList.Count - 1;
        }

        int index = nextCheckpointIndexList[carIndex];
        int checkpointIndex = checkpointList.IndexOf(checkpoint);

        if (index == checkpointIndex)
        {
            OnCarCorrectCheckpoint?.Invoke(this, new CarCheckpointEventArgs { carTransform = carTransform });
            nextCheckpointIndexList[carIndex] = (nextCheckpointIndexList[carIndex] + 1) % checkpointList.Count;
        } else {
            int n = checkpointList.Count;
            if (n > 0)
            {
                int last1 = (index - 1 + n) % n;
                int last2 = (index - 2 + n) % n;
                int last3 = (index - 3 + n) % n;

                if (checkpointIndex == last1 || checkpointIndex == last2 || checkpointIndex == last3)
                {
                    return;
                }
            }

            Debug.Log($"Wrong Checkpoint {carTransform.name}! Expected {index}, got {checkpointIndex}");
            OnCarWrongCheckpoint?.Invoke(this, new CarCheckpointEventArgs { carTransform = carTransform });
        }
    }

    public void ResetCar(Transform carTransform)
    {
        int carIndex = carList.IndexOf(carTransform);
        if (carIndex != -1)
        {
            nextCheckpointIndexList[carIndex] = 0;
        }
    }

    public CheckpointSingle GetNextCheckpoint(Transform carTransform)
    {
        int carIndex = carList.IndexOf(carTransform);
        if (carIndex != -1 && checkpointList.Count > 0)
        {
            return checkpointList[nextCheckpointIndexList[carIndex]];
        }
        return null;
    }
}
