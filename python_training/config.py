from dataclasses import dataclass
from typing import Optional


@dataclass
class PPOConfig:
    # --- Environment ---
    env_path: Optional[str] = None          # None = connect to Editor
    behavior_name: str = "AI Driver"        # Must match Unity BehaviorParameters
    time_scale: float = 20.0                # Unity time multiplier for training
    no_graphics: bool = False               # Disable rendering for speed

    # --- PPO ---
    batch_size: int = 1024
    buffer_size: int = 10240                # Steps collected before each update
    learning_rate: float = 3e-4
    lr_schedule: str = "linear"             # "linear" or "constant"
    beta: float = 0.005                     # Entropy coefficient
    epsilon: float = 0.2                    # PPO clip range
    lambd: float = 0.95                     # GAE lambda
    gamma: float = 0.99                     # Discount factor
    num_epoch: int = 4                      # Optimisation epochs per update
    max_grad_norm: float = 0.5              # Gradient clipping

    # --- Normalization ---
    normalize_obs: bool = True              # Running mean/std normalisation

    # --- Training schedule ---
    max_steps: int = 10_000_000
    time_horizon: int = 128                 # Max trajectory length before bootstrap
    summary_freq: int = 50_000
    save_freq: int = 200_000
    keep_checkpoints: int = 5

    # --- Paths ---
    run_id: str = "CarRacing_Custom"
    model_dir: str = "results"              # Checkpoints: results/<run_id>/checkpoints/
    log_dir: str = "results"                # TensorBoard:  results/<run_id>/logs/

    # --- Resume / Initialize ---
    resume: bool = False                    # Automatically resume from latest checkpoint of run_id
    export_onnx: bool = False               # Export to ONNX immediately and exit
    resume_from: Optional[str] = None       # Full resume: weights + optimizer + step count
    initialize_from: Optional[str] = None   # Run-id to copy weights from (fresh optimizer + step=0)
