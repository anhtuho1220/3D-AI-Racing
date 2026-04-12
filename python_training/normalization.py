import numpy as np


class RunningMeanStd:
    """Welford's online algorithm for running mean / variance.
    Matches ML-Agents normalizer behaviour including clip to [-5, 5]."""

    def __init__(self, shape: tuple, epsilon: float = 1e-4):
        self.mean = np.zeros(shape, dtype=np.float64)
        self.var = np.ones(shape, dtype=np.float64)
        self.count = epsilon

    def update(self, batch: np.ndarray):
        batch_mean = np.mean(batch, axis=0)
        batch_var = np.var(batch, axis=0)
        batch_count = batch.shape[0]
        self._update_from_moments(batch_mean, batch_var, batch_count)

    def _update_from_moments(self, batch_mean, batch_var, batch_count):
        delta = batch_mean - self.mean
        total = self.count + batch_count
        new_mean = self.mean + delta * batch_count / total
        m_a = self.var * self.count
        m_b = batch_var * batch_count
        m2 = m_a + m_b + np.square(delta) * self.count * batch_count / total
        self.mean = new_mean
        self.var = m2 / total
        self.count = total

    def normalize(self, x: np.ndarray) -> np.ndarray:
        normed = (x - self.mean.astype(np.float32)) / (
            np.sqrt(self.var.astype(np.float32)) + 1e-8
        )
        return np.clip(normed, -5.0, 5.0)

    def state_dict(self):
        return {"mean": self.mean, "var": self.var, "count": self.count}

    def load_state_dict(self, d):
        self.mean = d["mean"]
        self.var = d["var"]
        self.count = d["count"]
