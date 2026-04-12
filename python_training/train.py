"""
Requirements:
    pip install mlagents-envs torch numpy tensorboard

Usage:
    1. Open the Unity project and press Play (or point --env to a built executable)
    2. Run:  python train.py
    3. Monitor:  tensorboard --logdir runs/

The observation space is auto-detected from Unity at runtime, so the script
adapts to any ray-sensor configuration you set in the Inspector.
"""

import argparse

from config import PPOConfig
from trainer import PPOTrainer


def parse_args() -> PPOConfig:
    parser = argparse.ArgumentParser(
        description="Custom PyTorch PPO trainer for Unity ML-Agents Car Racing"
    )
    parser.add_argument("--env", type=str, default=None,
                        help="Path to Unity build. None = connect to Editor.")
    parser.add_argument("--run-id", type=str, default="CarRacing_Custom",
                        help="Unique run identifier.")
    parser.add_argument("--resume", action="store_true",
                        help="Automatically resume from the latest checkpoint for this run-id.")
    parser.add_argument("--resume-from", type=str, default=None,
                        help="Path to .pt checkpoint to fully resume from.")
    parser.add_argument("--initialize-from", type=str, default=None,
                        help="Run-id to load weights from (fresh optimizer, step=0).")
    parser.add_argument("--export-onnx", action="store_true",
                        help="Export the specified model to ONNX immediately and exit.")
    parser.add_argument("--max-steps", type=int, default=10_000_000,
                        help="Total training steps.")
    parser.add_argument("--time-scale", type=float, default=20.0,
                        help="Unity time scale (higher = faster training).")
    parser.add_argument("--no-graphics", action="store_true",
                        help="Disable Unity rendering for faster training.")
    parser.add_argument("--batch-size", type=int, default=1024)
    parser.add_argument("--buffer-size", type=int, default=10240)
    parser.add_argument("--lr", type=float, default=3e-4)
    parser.add_argument("--gamma", type=float, default=0.99)
    parser.add_argument("--lambd", type=float, default=0.95)
    parser.add_argument("--epsilon", type=float, default=0.2)
    parser.add_argument("--beta", type=float, default=0.005)
    parser.add_argument("--num-epoch", type=int, default=4)
    parser.add_argument("--device", type=str, default=None,
                        help="Force device: 'cuda' or 'cpu'.")

    args = parser.parse_args()

    cfg = PPOConfig(
        env_path=args.env,
        run_id=args.run_id,
        resume=args.resume,
        export_onnx=args.export_onnx,
        resume_from=args.resume_from,
        initialize_from=args.initialize_from,
        max_steps=args.max_steps,
        time_scale=args.time_scale,
        no_graphics=args.no_graphics,
        batch_size=args.batch_size,
        buffer_size=args.buffer_size,
        learning_rate=args.lr,
        gamma=args.gamma,
        lambd=args.lambd,
        epsilon=args.epsilon,
        beta=args.beta,
        num_epoch=args.num_epoch,
    )
    return cfg


if __name__ == "__main__":
    cfg = parse_args()
    trainer = PPOTrainer(cfg)
    
    if cfg.export_onnx:
        import pathlib
        onnx_file = str(pathlib.Path(cfg.model_dir) / cfg.run_id / f"{cfg.run_id}.onnx")
        trainer.export_onnx(onnx_file)
        
        try:
            trainer.env.close()
        except:
            pass
        exit(0)
        
    trainer.train()
