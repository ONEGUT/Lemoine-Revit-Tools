"""Port of Core/LayoutConfig.cs + the scaling the engine applies before layout.

The C# engine builds the core config by multiplying every paper-space value by the
view scale (BuildCoreConfig), so the core works in model feet throughout. `scaled()`
reproduces that.
"""


class LayoutConfig:
    def __init__(self):
        # Spacing / precision — paper-space (sheet feet) until scaled().
        self.string_spacing_ft = (1.0 / 4.0) / 12.0     # 1/4"
        self.first_offset_ft   = (3.0 / 8.0) / 12.0     # 3/8"
        self.precision_ft      = (1.0 / 8.0) / 12.0     # 1/8"
        self.text_height_ft    = (3.0 / 32.0) / 12.0    # 3/32"

        # Witness anatomy — paper-space.
        self.witness_gap_ft       = (1.0 / 16.0) / 12.0
        self.witness_overshoot_ft = (1.0 / 8.0) / 12.0

        # Moved-tag column geometry, in multiples of text height (unitless).
        self.tag_column_base_heights  = 2.2
        self.tag_column_step_heights  = 1.4
        self.tag_column_along_heights = 0.75

        # Scoring weights (unitless).
        self.overlap_weight          = 1000.0
        self.off_crop_weight         = 1000.0
        self.witness_cross_weight    = 500.0
        self.crossing_weight         = 800.0
        self.leader_cross_weight     = 25.0
        self.leader_line_cross_weight = 10.0
        self.leader_slack_weight     = 1.0
        self.cramped_weight          = 3.0
        self.uneven_spacing_weight   = 5.0
        self.leader_weight           = 40.0
        self.region_weight           = 15.0

        # Stacking / refinement.
        self.max_repair_passes    = 3
        self.align_shared_rows    = True
        self.stagger_stacked_text = True
        self.stagger_weight       = 2.0

        # Convergence.
        self.max_iterations   = 50
        self.time_cap_ms      = 4000
        self.plateau_epsilon  = 1e-4
        self.max_offset_steps = 8

    def clone(self):
        c = LayoutConfig()
        c.__dict__.update(self.__dict__)
        return c

    def scaled(self, scale, text_paper_ft=None):
        """Paper-space -> model feet at 1:`scale`, mirroring AutoDimensionEngine.BuildCoreConfig."""
        c = self.clone()
        s = max(float(scale), 1.0)
        if text_paper_ft is not None:
            c.text_height_ft = float(text_paper_ft)
        # precision_ft is a model-space display tolerance and is deliberately NOT scaled
        # (matches AutoDimensionEngine.BuildCoreConfig).
        for k in ("string_spacing_ft", "first_offset_ft", "text_height_ft",
                  "witness_gap_ft", "witness_overshoot_ft"):
            setattr(c, k, getattr(c, k) * s)
        return c
