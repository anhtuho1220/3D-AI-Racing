using UnityEngine;
using System.Collections.Generic;

public class TrackCheckpoints : MonoBehaviour
{
    private List<CheckpointSingle> checkpointList;
    private int nextCheckpointIndex;
    private void Awake()
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
        nextCheckpointIndex = 0;
    }

    public void CheckpointTriggered(CheckpointSingle checkpoint)
    {
        int index = checkpointList.IndexOf(checkpoint);
        if (index == nextCheckpointIndex)
        {
            nextCheckpointIndex = (nextCheckpointIndex + 1) % checkpointList.Count;
            Debug.Log($"Checkpoint!" + index);
        } else {
            Debug.Log($"Wrong Checkpoint!");
        }
    }
}
