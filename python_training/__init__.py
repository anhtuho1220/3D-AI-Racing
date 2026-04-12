"""
python_training — Custom PyTorch PPO trainer for Unity ML-Agents.

Modules:
    config          PPOConfig dataclass
    normalization   RunningMeanStd (observation normaliser)
    network         PPONetwork (actor-critic)
    buffer          RolloutBuffer (rollout storage + GAE)
    trainer         PPOTrainer (full training loop)
    train           CLI entry point
"""

from config import PPOConfig
from normalization import RunningMeanStd
from network import PPONetwork
from buffer import RolloutBuffer
from trainer import PPOTrainer

__all__ = [
    "PPOConfig",
    "RunningMeanStd",
    "PPONetwork",
    "RolloutBuffer",
    "PPOTrainer",
]
