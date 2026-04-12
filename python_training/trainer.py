"""
PPO Trainer — manages the full training loop: env interaction → rollout → optimise.
"""

import time
from collections import deque
from pathlib import Path

import numpy as np
import torch
import torch.nn as nn
import torch.nn.functional as F
from torch.utils.tensorboard import SummaryWriter

from mlagents_envs.environment import UnityEnvironment
from mlagents_envs.base_env import ActionTuple
from mlagents_envs.exception import (
    UnityCommunicationException,
    UnityCommunicatorStoppedException,
)
from mlagents_envs.side_channel.engine_configuration_channel import (
    EngineConfigurationChannel,
)

from config import PPOConfig
from normalization import RunningMeanStd
from ML_model import PPONetwork
from buffer import RolloutBuffer


class PPOTrainer:
    """Manages the full training loop: env interaction → rollout → optimise."""

    def __init__(self, cfg: PPOConfig):
        self.cfg = cfg
        self.device = torch.device("cuda" if torch.cuda.is_available() else "cpu")
        print(f"[INFO] Using device: {self.device}")

        # ── Create Unity env ──
        engine_channel = EngineConfigurationChannel()
        self.env = UnityEnvironment(
            file_name=cfg.env_path,
            no_graphics=cfg.no_graphics,
            side_channels=[engine_channel],
        )
        engine_channel.set_configuration_parameters(
            time_scale=cfg.time_scale,
            target_frame_rate=-1,  # Uncap FPS — lets Unity skip frames when behind
            quality_level=0,  # Lowest render quality during training
            width=84,  # Match mlagents-learn defaults (84x84)
            height=84,  # Tiny render target = much less GPU load
            capture_frame_rate=60,
        )
        self.env.reset()

        # ── Discover behaviour spec ──
        behavior_names = list(self.env.behavior_specs.keys())
        print(f"[INFO] Available behaviours: {behavior_names}")

        # Allow partial match on behavior name
        self.behavior_name = None
        for name in behavior_names:
            if cfg.behavior_name in name:
                self.behavior_name = name
                break
        if self.behavior_name is None:
            self.behavior_name = behavior_names[0]
            print(
                f"[WARN] '{cfg.behavior_name}' not found, using '{self.behavior_name}'"
            )

        spec = self.env.behavior_specs[self.behavior_name]

        # ── Parse observation shapes (from specs — may not include stacking) ──
        print(f"\n[INFO] Observation specs for '{self.behavior_name}':")
        spec_obs_size = 0
        self.obs_shapes = []
        for i, obs_spec in enumerate(spec.observation_specs):
            shape = obs_spec.shape
            dim_type = obs_spec.dimension_property
            name = getattr(obs_spec, "name", f"obs_{i}")
            size = int(np.prod(shape))
            self.obs_shapes.append(shape)
            spec_obs_size += size
            print(f"  [{i}] name={name}  shape={shape}  size={size}  dtype={dim_type}")

        # ── Detect actual obs_dim from runtime data (specs may not include stacking) ──
        print("[INFO] Probing environment for actual observation sizes...")
        actual_obs_size = None
        action_spec = spec.action_spec
        for probe_step in range(100):
            decision_steps, terminal_steps = self.env.get_steps(self.behavior_name)
            if len(decision_steps) > 0:
                actual_obs_size = sum(
                    obs[0].flatten().shape[0] for obs in decision_steps.obs
                )
                self.sensor_dims = [obs.shape[1] for obs in decision_steps.obs]
                print(
                    f"[INFO] Got agents at probe step {probe_step}, obs shapes: "
                    f"{[obs.shape for obs in decision_steps.obs]}"
                )
                # Must provide actions before stepping
                n = len(decision_steps)
                random_cont = np.zeros(
                    (n, action_spec.continuous_size), dtype=np.float32
                )
                random_disc = np.zeros(
                    (n, len(action_spec.discrete_branches)), dtype=np.int32
                )
                self.env.set_actions(
                    self.behavior_name, ActionTuple(random_cont, random_disc)
                )
                break
            self.env.step()

        if actual_obs_size is not None:
            self.obs_dim = actual_obs_size
            if actual_obs_size != spec_obs_size:
                print(
                    f"[INFO] Spec obs size: {spec_obs_size}, actual runtime obs size: {actual_obs_size}"
                )
                print(f"[INFO] Using runtime size (observation stacking detected)")
        else:
            self.obs_dim = spec_obs_size
            print(
                f"[WARN] Could not detect runtime obs size after 100 steps, using spec size: {spec_obs_size}"
            )

        print(f"[INFO] Total flattened observation size: {self.obs_dim}")

        # Reset after probing so training starts clean
        self.env.reset()

        # ── Parse action spec ──
        self.cont_dim = spec.action_spec.continuous_size
        self.disc_branches = (
            list(spec.action_spec.discrete_branches)
            if spec.action_spec.discrete_branches
            else []
        )
        self.disc_dim = len(self.disc_branches)
        print(
            f"[INFO] Actions — continuous: {self.cont_dim}, discrete branches: {self.disc_branches}"
        )

        # ── Build network ──
        self.network = PPONetwork(
            obs_size=self.obs_dim,
            continuous_size=self.cont_dim,
            discrete_branches=self.disc_branches if self.disc_branches else [],
        ).to(self.device)
        print(f"\n[INFO] Network architecture:\n{self.network}\n")

        # ── Optimizer ──
        self.optimizer = torch.optim.Adam(
            self.network.parameters(), lr=cfg.learning_rate, eps=1e-5
        )

        # ── CPU inference copy (avoids GPU transfer overhead during rollouts) ──
        self.inference_device = torch.device("cpu")
        self.inference_network = PPONetwork(
            obs_size=self.obs_dim,
            continuous_size=self.cont_dim,
            discrete_branches=self.disc_branches if self.disc_branches else [],
        ).to(self.inference_device)
        self.inference_network.eval()
        self._sync_inference_network()

        # ── Observation normaliser ──
        self.obs_normalizer = (
            RunningMeanStd((self.obs_dim,)) if cfg.normalize_obs else None
        )

        # ── Rollout buffer ──
        self.buffer = RolloutBuffer(
            cfg.buffer_size, self.obs_dim, self.cont_dim, self.disc_dim
        )

        # ── Pre-allocated arrays for rollout (avoid per-step allocations) ──
        self._zero_cont = np.zeros(self.cont_dim, dtype=np.float32)
        self._zero_disc = np.zeros(self.disc_dim, dtype=np.int64)

        # ── Logging ──
        log_path = Path(cfg.log_dir) / cfg.run_id / "logs"
        self.writer = SummaryWriter(str(log_path))
        self.global_step = 0
        self.episode_rewards = deque(maxlen=100)
        self.episode_lengths = deque(maxlen=100)

        # ── Per-agent trackers (for multi-agent) ──
        self._agent_rewards: dict[int, float] = {}
        self._agent_steps: dict[int, int] = {}
        self._agent_cache: dict[int, dict] = {}

        # ── Checkpoint management ──
        self.model_path = Path(cfg.model_dir) / cfg.run_id / "checkpoints"
        self.model_path.mkdir(parents=True, exist_ok=True)
        self._saved_checkpoints: deque = deque(maxlen=cfg.keep_checkpoints)

        # ── Resume or Initialize ──
        if cfg.resume:
            latest_ckpt = self._find_latest_checkpoint(cfg.run_id)
            if latest_ckpt:
                self._load_checkpoint(str(latest_ckpt))
            else:
                print(f"[WARN] --resume used but no checkpoints found for '{cfg.run_id}'. Starting from scratch.")
        elif cfg.resume_from:
            self._load_checkpoint(cfg.resume_from)
        elif cfg.initialize_from:
            self._initialize_from_run(cfg.initialize_from)

    # ─────────────────────── helpers ───────────────────────

    def _sync_inference_network(self):
        """Copy trained weights to the CPU inference network."""
        self.inference_network.load_state_dict(self.network.state_dict())
        self.inference_network.eval()

    def _flatten_obs_batch(self, obs_list: list[np.ndarray]) -> np.ndarray:
        """Vectorized: flatten and concatenate all sensors for all agents at once."""
        parts = [obs.reshape(obs.shape[0], -1) for obs in obs_list]
        return np.concatenate(parts, axis=1).astype(np.float32)

    def _normalize_batch(self, obs_batch: np.ndarray) -> np.ndarray:
        """Vectorized normalization for entire batch."""
        if self.obs_normalizer is not None:
            return self.obs_normalizer.normalize(obs_batch)
        return obs_batch

    def _lr_schedule(self):
        """Linear LR decay from initial to 0 over max_steps."""
        if self.cfg.lr_schedule == "linear":
            frac = 1.0 - self.global_step / self.cfg.max_steps
            lr = max(self.cfg.learning_rate * frac, 1e-7)
            for pg in self.optimizer.param_groups:
                pg["lr"] = lr
            return lr
        return self.cfg.learning_rate

    # ─────────────────── checkpoint I/O ────────────────────

    def _find_latest_checkpoint(self, run_id: str) -> Path | None:
        """Find the most recent .pt checkpoint for a given run-id."""
        ckpt_dir = Path(self.cfg.model_dir) / run_id / "checkpoints"
        if not ckpt_dir.exists():
            return None
        checkpoints = sorted(ckpt_dir.glob("*.pt"), key=lambda p: p.stat().st_mtime)
        return checkpoints[-1] if checkpoints else None

    def _save_checkpoint(self, tag: str = ""):
        fname = (
            f"checkpoint_{tag}_{self.global_step}.pt"
            if tag
            else f"checkpoint_{self.global_step}.pt"
        )
        path = self.model_path / fname
        state = {
            "global_step": self.global_step,
            "network": self.network.state_dict(),
            "optimizer": self.optimizer.state_dict(),
        }
        if self.obs_normalizer:
            state["obs_normalizer"] = self.obs_normalizer.state_dict()
        torch.save(state, path)
        print(f"[SAVE] {path}")

        self._saved_checkpoints.append(path)
        # Prune old checkpoints
        while len(self._saved_checkpoints) > self.cfg.keep_checkpoints:
            old = self._saved_checkpoints.popleft()
            if old.exists():
                old.unlink()

    def _load_checkpoint(self, path_str: str):
        """Full resume: weights + optimizer + step count."""
        path = Path(path_str)
        if not path.exists():
            print(f"[WARN] Checkpoint not found: {path}")
            return
        ckpt = torch.load(path, map_location=self.device, weights_only=False)
        self.network.load_state_dict(ckpt["network"])
        self.optimizer.load_state_dict(ckpt["optimizer"])
        self.global_step = ckpt.get("global_step", 0)
        if self.obs_normalizer and "obs_normalizer" in ckpt:
            self.obs_normalizer.load_state_dict(ckpt["obs_normalizer"])
        self._sync_inference_network()
        print(f"[LOAD] Resumed from {path}  (step {self.global_step})")

    def _initialize_from_run(self, run_id: str):
        """Load only network weights from a previous run (fresh optimizer, step=0).
        Detects both our custom checkpoint format and mlagents-learn format."""
        # Try our format first: results/<run_id>/checkpoints/*.pt
        ckpt_path = self._find_latest_checkpoint(run_id)
        if ckpt_path is not None:
            ckpt = torch.load(ckpt_path, map_location=self.device, weights_only=False)
            self.network.load_state_dict(ckpt["network"])
            if self.obs_normalizer and "obs_normalizer" in ckpt:
                self.obs_normalizer.load_state_dict(ckpt["obs_normalizer"])
            self._sync_inference_network()
            print(
                f"[INIT] Weights loaded from '{run_id}' ({ckpt_path.name})  — optimizer reset, step=0"
            )
            return

        # Try mlagents-learn format: results/<run_id>/<behavior>/checkpoint.pt
        mlagents_dir = Path(self.cfg.model_dir) / run_id
        if mlagents_dir.exists():
            # Search for checkpoint.pt in subdirectories
            candidates = list(mlagents_dir.rglob("checkpoint.pt"))
            if not candidates:
                # Also try timestamped checkpoints
                candidates = sorted(
                    mlagents_dir.rglob("*.pt"), key=lambda p: p.stat().st_mtime
                )
            if candidates:
                ckpt_path = candidates[-1]
                ckpt = torch.load(
                    ckpt_path, map_location=self.device, weights_only=False
                )
                if "Policy" in ckpt:
                    self._load_mlagents_checkpoint(ckpt, ckpt_path)
                    return
                elif "network" in ckpt:
                    self.network.load_state_dict(ckpt["network"])
                    self._sync_inference_network()
                    print(f"[INIT] Weights loaded from '{run_id}' ({ckpt_path.name})")
                    return

        print(f"[WARN] No checkpoints found for run '{run_id}'")

    def _load_mlagents_checkpoint(self, ckpt: dict, path: Path):
        """Convert and load weights from an mlagents-learn checkpoint."""
        policy = ckpt["Policy"]
        critic = ckpt.get("Optimizer:critic", {})

        state = {}

        # ── Actor encoder ──
        # mlagents: network_body._body_endoder.seq_layers.{0,2}.weight/bias
        # ours:     actor_encoder.{0,2}.weight/bias
        for idx in [0, 2]:
            key_w = f"network_body._body_endoder.seq_layers.{idx}.weight"
            key_b = f"network_body._body_endoder.seq_layers.{idx}.bias"
            if key_w in policy:
                state[f"actor_encoder.{idx}.weight"] = policy[key_w]
                state[f"actor_encoder.{idx}.bias"] = policy[key_b]

        # ── Critic encoder (from Optimizer:critic) ──
        for idx in [0, 2]:
            key_w = f"network_body._body_endoder.seq_layers.{idx}.weight"
            key_b = f"network_body._body_endoder.seq_layers.{idx}.bias"
            if key_w in critic:
                state[f"critic_encoder.{idx}.weight"] = critic[key_w]
                state[f"critic_encoder.{idx}.bias"] = critic[key_b]
            elif key_w in policy:
                # Fallback: use policy encoder for critic too
                state[f"critic_encoder.{idx}.weight"] = policy[key_w]
                state[f"critic_encoder.{idx}.bias"] = policy[key_b]

        # ── Continuous action head ──
        if "action_model._continuous_distribution.mu.weight" in policy:
            state["cont_mean.weight"] = policy[
                "action_model._continuous_distribution.mu.weight"
            ]
            state["cont_mean.bias"] = policy[
                "action_model._continuous_distribution.mu.bias"
            ]
            state["cont_log_std"] = policy[
                "action_model._continuous_distribution.log_sigma"
            ]

        # ── Discrete action heads ──
        for i in range(len(self.disc_branches)):
            key_w = f"action_model._discrete_distribution.branches.{i}.weight"
            key_b = f"action_model._discrete_distribution.branches.{i}.bias"
            if key_w in policy:
                state[f"disc_heads.{i}.weight"] = policy[key_w]
                state[f"disc_heads.{i}.bias"] = policy[key_b]

        # ── Value head (from critic) ──
        if "value_heads.value_heads.extrinsic.weight" in critic:
            state["value_head.weight"] = critic[
                "value_heads.value_heads.extrinsic.weight"
            ]
            state["value_head.bias"] = critic["value_heads.value_heads.extrinsic.bias"]

        self.network.load_state_dict(state, strict=False)
        loaded_keys = [k for k in state if k in dict(self.network.named_parameters())]
        print(
            f"[INIT] Loaded {len(loaded_keys)} weight tensors from mlagents-learn checkpoint: {path.name}"
        )

        # ── Reconstruct normalizer from per-sensor stats ──
        if self.obs_normalizer:
            means, variances, steps = [], [], []
            for i in range(20):  # Up to 20 sensors
                key_mean = f"network_body.processors.{i}.normalizer.running_mean"
                key_var = f"network_body.processors.{i}.normalizer.running_variance"
                key_steps = (
                    f"network_body.processors.{i}.normalizer.normalization_steps"
                )
                if key_mean in policy:
                    means.append(policy[key_mean].cpu().numpy())
                    variances.append(policy[key_var].cpu().numpy())
                    steps.append(policy[key_steps].item())
                else:
                    break
            if means:
                combined_mean = np.concatenate(means).astype(np.float64)
                combined_var = np.concatenate(variances).astype(np.float64)
                avg_steps = np.mean(steps)
                if len(combined_mean) == self.obs_dim:
                    self.obs_normalizer.mean = combined_mean
                    self.obs_normalizer.var = combined_var / max(avg_steps, 1)
                    self.obs_normalizer.count = avg_steps
                    print(
                        f"[INIT] Restored normalizer from {len(means)} sensor stats "
                        f"({avg_steps:.0f} steps)"
                    )

        self._sync_inference_network()
        print(f"[INIT] mlagents-learn model loaded — optimizer reset, step=0")

    def export_onnx(self, path: str):
        """Export model to ONNX format compatible with Unity ML-Agents Model behaviors."""
        from ML_model import ONNXExporterWrapper
        
        mean = self.obs_normalizer.mean if self.obs_normalizer else None
        var = self.obs_normalizer.var if self.obs_normalizer else None
        
        wrapper = ONNXExporterWrapper(self.network, mean, var).to(self.device).eval()

        dummy_inputs = tuple(
            torch.randn(1, size, device=self.device) for size in self.sensor_dims
        )
        
        input_names = [f"obs_{i}" for i in range(len(self.sensor_dims))]
        
        dynamic_axes = {name: {0: "batch"} for name in input_names}
        dynamic_axes["continuous_actions"] = {0: "batch"}

        torch.onnx.export(
            wrapper,
            dummy_inputs,
            path,
            input_names=input_names,
            output_names=["continuous_actions"],
            dynamic_axes=dynamic_axes,
        )
        print(f"[EXPORT] ONNX model saved to {path}")

    # ──────────────────── rollout collection ────────────────────

    def _collect_rollouts(self):
        """
        Interact with Unity to fill the rollout buffer.
        Handles multiple agents with the same behavior.
        Optimized: CPU inference + vectorized ops to minimize Unity wait time.
        """
        self.buffer.reset()
        # Sync CPU inference network with latest trained weights
        self._sync_inference_network()

        while not self.buffer.full:
            decision_steps, terminal_steps = self.env.get_steps(self.behavior_name)

            # ── Process terminal agents first (record episode stats) ──
            for agent_id in terminal_steps.agent_id:
                idx = terminal_steps.agent_id_to_index[agent_id]
                reward = float(terminal_steps.reward[idx])

                if agent_id in self._agent_rewards:
                    self._agent_rewards[agent_id] += reward
                    self.episode_rewards.append(self._agent_rewards[agent_id])
                    self.episode_lengths.append(self._agent_steps.get(agent_id, 0))

                if agent_id in self._agent_cache and not self.buffer.full:
                    c = self._agent_cache[agent_id]
                    self.buffer.store(
                        agent_id=agent_id,
                        obs=c['obs'],
                        cont_act=c['cont_act'],
                        disc_act=c['disc_act'],
                        log_prob=c['log_prob'],
                        value=c['value'],
                        reward=reward,
                        done=1.0,
                    )
                    self.global_step += 1

                self._agent_rewards.pop(agent_id, None)
                self._agent_steps.pop(agent_id, None)
                self._agent_cache.pop(agent_id, None)

            # ── Process deciding agents ──
            n_agents = len(decision_steps)
            if n_agents == 0:
                self.env.step()
                continue

            for i, agent_id in enumerate(decision_steps.agent_id):
                agent_id = int(agent_id)
                reward = float(decision_steps.reward[i])

                if agent_id in self._agent_cache and not self.buffer.full:
                    c = self._agent_cache[agent_id]
                    self.buffer.store(
                        agent_id=agent_id,
                        obs=c['obs'],
                        cont_act=c['cont_act'],
                        disc_act=c['disc_act'],
                        log_prob=c['log_prob'],
                        value=c['value'],
                        reward=reward,
                        done=0.0,
                    )
                    self.global_step += 1

                if agent_id not in self._agent_rewards:
                    self._agent_rewards[agent_id] = 0.0
                    self._agent_steps[agent_id] = 0
                self._agent_rewards[agent_id] += reward
                self._agent_steps[agent_id] += 1

            # ── Python processing (obs → actions) ──

            all_obs = self._flatten_obs_batch(decision_steps.obs)

            if self.obs_normalizer:
                self.obs_normalizer.update(all_obs)

            normed_obs = self._normalize_batch(all_obs)

            obs_tensor = torch.from_numpy(normed_obs)
            with torch.inference_mode():
                cont_act, disc_act, log_prob, _, value = (
                    self.inference_network.get_action_and_value(obs_tensor)
                )
            cont_act_np = cont_act.numpy()

            # Clip continuous actions like ML-Agents before sending to Unity
            cont_act_clipped = np.clip(cont_act_np, -3.0, 3.0) / 3.0

            disc_act_np = disc_act.numpy().astype(np.int32)
            log_prob_np = log_prob.numpy()
            value_np = value.numpy()

            action_tuple = ActionTuple(
                continuous=cont_act_clipped.astype(np.float32),
                discrete=disc_act_np,
            )
            self.env.set_actions(self.behavior_name, action_tuple)

            for i, agent_id in enumerate(decision_steps.agent_id):
                self._agent_cache[int(agent_id)] = {
                    'obs': normed_obs[i].copy(),
                    'cont_act': cont_act_np[i].copy(),
                    'disc_act': disc_act_np[i].copy(),
                    'log_prob': float(log_prob_np[i]),
                    'value': float(value_np[i])
                }

            # ── Step Unity ──
            self.env.step()

        # ── Bootstrap value for last step ──
        decision_steps, _ = self.env.get_steps(self.behavior_name)
        last_values = {}
        if len(decision_steps) > 0:
            obs = self._flatten_obs_batch(decision_steps.obs)
            obs = self._normalize_batch(obs)
            obs_t = torch.from_numpy(obs)
            with torch.inference_mode():
                values = self.inference_network.get_value(obs_t).cpu().numpy()
            for i, agent_id in enumerate(decision_steps.agent_id):
                last_values[int(agent_id)] = float(values[i])

        self.buffer.compute_gae(last_values, self.cfg.gamma, self.cfg.lambd)

    # ──────────────────── PPO update ────────────────────

    def _update(self):
        """Run PPO optimisation on the filled buffer."""
        cfg = self.cfg
        buf = self.buffer
        n = buf.ptr

        # Convert to tensors
        obs_t = torch.from_numpy(buf.obs[:n]).to(self.device)
        cont_t = torch.from_numpy(buf.cont_actions[:n]).to(self.device)
        disc_t = torch.from_numpy(buf.disc_actions[:n]).to(self.device)
        old_lp_t = torch.from_numpy(buf.log_probs[:n]).to(self.device)
        adv_t = torch.from_numpy(buf.advantages[:n]).to(self.device)
        ret_t = torch.from_numpy(buf.returns[:n]).to(self.device)

        # Normalize advantages
        adv_t = (adv_t - adv_t.mean()) / (adv_t.std() + 1e-8)

        total_pg_loss = 0
        total_vf_loss = 0
        total_entropy = 0
        total_updates = 0

        for epoch in range(cfg.num_epoch):
            for batch_idx in buf.get_batches(cfg.batch_size):
                b_obs = obs_t[batch_idx]
                b_cont = cont_t[batch_idx]
                b_disc = disc_t[batch_idx]
                b_old_lp = old_lp_t[batch_idx]
                b_adv = adv_t[batch_idx]
                b_ret = ret_t[batch_idx]

                _, _, new_lp, entropy, new_val = self.network.get_action_and_value(
                    b_obs, cont_actions=b_cont, disc_actions=b_disc
                )

                # ── Policy loss (clipped surrogate) ──
                log_ratio = new_lp - b_old_lp
                ratio = log_ratio.exp()
                surr1 = ratio * b_adv
                surr2 = torch.clamp(ratio, 1.0 - cfg.epsilon, 1.0 + cfg.epsilon) * b_adv
                pg_loss = -torch.min(surr1, surr2).mean()

                # ── Value loss ──
                vf_loss = F.mse_loss(new_val, b_ret)

                # ── Entropy bonus ──
                ent_loss = -entropy.mean()

                # ── Total loss ──
                loss = pg_loss + 0.5 * vf_loss + cfg.beta * ent_loss

                self.optimizer.zero_grad()
                loss.backward()
                nn.utils.clip_grad_norm_(self.network.parameters(), cfg.max_grad_norm)
                self.optimizer.step()

                total_pg_loss += pg_loss.item()
                total_vf_loss += vf_loss.item()
                total_entropy += (-ent_loss).item()
                total_updates += 1

        # ── Log ──
        if total_updates > 0:
            self.writer.add_scalar(
                "losses/policy_loss", total_pg_loss / total_updates, self.global_step
            )
            self.writer.add_scalar(
                "losses/value_loss", total_vf_loss / total_updates, self.global_step
            )
            self.writer.add_scalar(
                "losses/entropy", total_entropy / total_updates, self.global_step
            )

    # ──────────────────── main loop ────────────────────

    def train(self):
        """Main training loop."""
        print(f"\n{'=' * 60}")
        print(f"  Starting PPO Training: {self.cfg.run_id}")
        print(f"  Max steps: {self.cfg.max_steps:,}")
        print(f"  Device: {self.device}")
        print(f"{'=' * 60}\n")

        last_summary_step = 0
        last_save_step = 0
        start_time = time.time()

        try:
            while self.global_step < self.cfg.max_steps:
                # LR schedule
                current_lr = self._lr_schedule()

                # Collect
                self._collect_rollouts()

                # Update (Unity is frozen during this)
                self._update()

                # ── Periodic logging ──
                if self.global_step - last_summary_step >= self.cfg.summary_freq:
                    last_summary_step = self.global_step
                    elapsed = time.time() - start_time
                    sps = self.global_step / max(elapsed, 1)

                    mean_reward = (
                        np.mean(self.episode_rewards) if self.episode_rewards else 0
                    )
                    mean_length = (
                        np.mean(self.episode_lengths) if self.episode_lengths else 0
                    )

                    self.writer.add_scalar(
                        "charts/mean_reward", mean_reward, self.global_step
                    )
                    self.writer.add_scalar(
                        "charts/mean_ep_length", mean_length, self.global_step
                    )
                    self.writer.add_scalar(
                        "charts/learning_rate", current_lr, self.global_step
                    )
                    self.writer.add_scalar(
                        "charts/steps_per_sec", sps, self.global_step
                    )

                    progress = self.global_step / self.cfg.max_steps * 100
                    print(
                        f"[Step {self.global_step:>10,} / {self.cfg.max_steps:,}]  "
                        f"{progress:5.1f}%  |  "
                        f"reward={mean_reward:+8.2f}  |  "
                        f"ep_len={mean_length:6.0f}  |  "
                        f"lr={current_lr:.2e}  |  "
                        f"SPS={sps:.0f}"
                    )

                # ── Periodic save ──
                if self.global_step - last_save_step >= self.cfg.save_freq:
                    last_save_step = self.global_step
                    self._save_checkpoint()

        except KeyboardInterrupt:
            print("\n[INFO] Training interrupted by user.")
        except (UnityCommunicatorStoppedException, UnityCommunicationException) as e:
            print(f"\n[ERROR] Unity disconnected: {e}")
            print(
                "[INFO] This usually means the Unity Editor was stopped or the build was closed."
            )
            print("[INFO] Saving checkpoint before exiting...")

        # Final save
        self._save_checkpoint(tag="final")
        
        # Automatically export ONNX at the end of training
        onnx_path = str(self.model_path.parent / f"{self.cfg.run_id}.onnx")
        self.export_onnx(onnx_path)
        self.writer.close()
        try:
            self.env.close()
        except Exception:
            pass  # env already disconnected
        print(f"\n[DONE] Training ended at step {self.global_step:,}")
