import torch
import torch.nn as nn
from torch.distributions import Normal, Categorical


class ONNXExporterWrapper(nn.Module):
    """Wraps PPO network with Unity ML-Agents specific requirements (normalisation, version constants)."""
    def __init__(self, network, mean=None, var=None):
        super().__init__()
        self.network = network
        
        self.register_buffer("version_number", torch.tensor([3], dtype=torch.int64))
        self.register_buffer("memory_size", torch.tensor([0], dtype=torch.int64))

        if mean is not None and var is not None:
            self.register_buffer("mean", torch.tensor(mean, dtype=torch.float32).unsqueeze(0))
            self.register_buffer("var", torch.tensor(var, dtype=torch.float32).unsqueeze(0))
            self.has_normalizer = True
        else:
            self.has_normalizer = False

    def forward(self, *obs_inputs):
        # Concatenate inputs from Unity (obs_0, obs_1, ...)
        x = torch.cat(obs_inputs, dim=1) if len(obs_inputs) > 1 else obs_inputs[0]

        if self.has_normalizer:
            std = torch.sqrt(self.var + 1e-8)
            x = (x - self.mean) / std
            x = torch.clamp(x, -5.0, 5.0)

        # Network automatically outputs [-1, 1] clipped continuous actions
        return self.network(x)

class PPONetwork(nn.Module):
    """
    Actor-Critic matching ML-Agents PyTorch architecture:
      • Separate encoder streams for actor and critic
      • Linear → Swish (no LayerNorm)
      • Hybrid continuous + discrete action heads
    """

    def __init__(
        self,
        obs_size: int,
        continuous_size: int,
        discrete_branches: list[int]
    ):
        super().__init__()
        self.continuous_size = continuous_size
        self.discrete_branches = discrete_branches

        # ── Actor encoder (policy stream) ──
        self.actor_encoder = nn.Sequential(
            nn.Linear(obs_size, 256),
            nn.SiLU(),
            nn.Linear(256, 256),
            nn.SiLU()
        )

        self.critic_encoder = nn.Sequential(
            nn.Linear(obs_size, 256),
            nn.SiLU(),
            nn.Linear(256, 256),
            nn.SiLU()
        )

        # ── Continuous action head ──
        if continuous_size > 0:
            self.cont_mean = nn.Linear(256, continuous_size)
            # Shape (1, C) to match ML-Agents log_sigma format
            self.cont_log_std = nn.Parameter(torch.zeros(1, continuous_size))

        # ── Discrete action heads ──
        self.disc_heads = nn.ModuleList(
            [nn.Linear(256, branch_size) for branch_size in discrete_branches]
        )

        # ── Value head ──
        self.value_head = nn.Linear(256, 1)

        self._init_weights()

    def _init_weights(self):
        for m in self.modules():
            if isinstance(m, nn.Linear):
                nn.init.kaiming_normal_(m.weight, nonlinearity='linear')
                if m.bias is not None:
                    nn.init.zeros_(m.bias)
        # Actor output layers get smaller init for stability
        if self.continuous_size > 0:
            nn.init.kaiming_normal_(self.cont_mean.weight, nonlinearity='linear')
        for head in self.disc_heads:
            nn.init.kaiming_normal_(head.weight, nonlinearity='linear')

    def forward(self, obs: torch.Tensor):
        """Returns deterministic continuous action for ONNX export."""
        h_actor = self.actor_encoder(obs)
        if self.continuous_size > 0:
            mean = self.cont_mean(h_actor)
            return torch.clamp(mean, -3.0, 3.0) / 3.0
        return torch.empty(obs.size(0), 0, device=obs.device)

    def get_value(self, obs: torch.Tensor) -> torch.Tensor:
        h = self.critic_encoder(obs)
        return self.value_head(h).squeeze(-1)

    def get_action_and_value(
        self, obs: torch.Tensor, cont_actions=None, disc_actions=None
    ):
        h_actor = self.actor_encoder(obs)
        h_critic = self.critic_encoder(obs)

        # ── Continuous ──
        cont_log_prob = torch.zeros(obs.shape[0], device=obs.device)
        cont_entropy = torch.zeros(obs.shape[0], device=obs.device)
        if self.continuous_size > 0:
            mean = self.cont_mean(h_actor)
            std = self.cont_log_std.exp().expand_as(mean)
            dist_cont = Normal(mean, std)
            if cont_actions is None:
                cont_actions = dist_cont.sample()
            cont_log_prob = dist_cont.log_prob(cont_actions).sum(dim=-1)
            cont_entropy = dist_cont.entropy().sum(dim=-1)

        # ── Discrete ──
        disc_log_prob = torch.zeros(obs.shape[0], device=obs.device)
        disc_entropy = torch.zeros(obs.shape[0], device=obs.device)
        disc_actions_list = []
        for i, head in enumerate(self.disc_heads):
            logits = head(h_actor)
            dist_disc = Categorical(logits=logits)
            if disc_actions is None:
                a = dist_disc.sample()
            else:
                a = disc_actions[:, i]
            disc_actions_list.append(a)
            disc_log_prob += dist_disc.log_prob(a)
            disc_entropy += dist_disc.entropy()

        if disc_actions is None and len(disc_actions_list) > 0:
            disc_actions = torch.stack(disc_actions_list, dim=-1)
        elif disc_actions is None:
            disc_actions = torch.empty(obs.shape[0], 0, device=obs.device)

        total_log_prob = cont_log_prob + disc_log_prob
        total_entropy = cont_entropy + disc_entropy
        value = self.value_head(h_critic).squeeze(-1)

        return cont_actions, disc_actions, total_log_prob, total_entropy, value
