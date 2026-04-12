import numpy as np
from collections import defaultdict

class RolloutBuffer:
    def __init__(self, buffer_size: int, obs_dim: int, cont_dim: int, disc_dim: int):
        self.buffer_size = buffer_size
        self.obs_dim = obs_dim
        self.cont_dim = cont_dim
        self.disc_dim = disc_dim
        self.reset()

    def reset(self):
        self.trajectories = defaultdict(list)
        self.ptr = 0
        
        self.obs = None
        self.cont_actions = None
        self.disc_actions = None
        self.log_probs = None
        self.rewards = None
        self.values = None
        self.dones = None
        self.advantages = None
        self.returns = None

    def store(self, agent_id: int, obs, cont_act, disc_act, log_prob, reward, value, done):
        self.trajectories[agent_id].append({
            'obs': obs,
            'cont_act': cont_act,
            'disc_act': disc_act,
            'log_prob': log_prob,
            'reward': reward,
            'value': value,
            'done': done,
        })
        self.ptr += 1

    @property
    def full(self):
        return self.ptr >= self.buffer_size

    def compute_gae(self, last_values: dict, gamma: float, lambd: float):
        all_obs, all_cont, all_disc = [], [], []
        all_lp, all_adv, all_ret = [], [], []
        all_val = []

        for agent_id, traj in self.trajectories.items():
            T = len(traj)
            adv = np.zeros(T, dtype=np.float32)
            ret = np.zeros(T, dtype=np.float32)
            
            last_adv = 0.0
            last_val = last_values.get(agent_id, 0.0) if hasattr(last_values, "get") else 0.0
            
            for t in reversed(range(T)):
                step = traj[t]
                if t == T - 1:
                    next_value = last_val
                else:
                    next_value = traj[t + 1]['value']
                
                next_non_terminal = 1.0 - step['done']
                
                delta = step['reward'] + gamma * next_value * next_non_terminal - step['value']
                adv[t] = last_adv = delta + gamma * lambd * next_non_terminal * last_adv
                ret[t] = adv[t] + step['value']
            
            all_obs.extend([s['obs'] for s in traj])
            all_cont.extend([s['cont_act'] for s in traj])
            all_disc.extend([s['disc_act'] for s in traj])
            all_lp.extend([s['log_prob'] for s in traj])
            all_val.extend([s['value'] for s in traj])
            all_adv.extend(adv)
            all_ret.extend(ret)

        self.obs = np.array(all_obs, dtype=np.float32)
        self.cont_actions = np.array(all_cont, dtype=np.float32)
        self.disc_actions = np.array(all_disc, dtype=np.int64)
        self.log_probs = np.array(all_lp, dtype=np.float32)
        self.values = np.array(all_val, dtype=np.float32)
        self.advantages = np.array(all_adv, dtype=np.float32)
        self.returns = np.array(all_ret, dtype=np.float32)

    def get_batches(self, batch_size: int):
        indices = np.arange(self.ptr)
        np.random.shuffle(indices)
        for start in range(0, self.ptr, batch_size):
            end = min(start + batch_size, self.ptr)
            yield indices[start:end]
