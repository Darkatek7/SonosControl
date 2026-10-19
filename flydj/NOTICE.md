# FlyDJ notices and attributions

FlyDJ is an experimental radio-station "DJ" that scores audio with a leaky
integrate-and-fire simulation of a real fruit-fly (*Drosophila melanogaster*)
brain connectome. The scores are an interpretive toy, not a measurement of what
a fly would hear or like. The following data, models and software are used.

## Connectome data (FlyWire, release 783)

- **Wiring diagram**: FlyWire female adult fly brain, public release 783.
  Dorkenwald et al. 2024, "Neuronal wiring diagram of an adult brain", Nature
  634:124-138, <https://doi.org/10.1038/s41586-024-07558-y>. Based on the
  EM volume of Zheng et al. 2018 (FAFB) and the FlyWire proofreading community.
  Licence: **CC BY 4.0** (the FlyWire connectivity data is deposited on Zenodo,
  record [10676866](https://zenodo.org/records/10676866), licence `cc-by-4.0`).
  FlyDJ ships a subset of it (and, as the optional `full` artifact, all of it) in derived form:
  a sparse graph of signed synapse counts.
- **Cell types and annotations**: Schlegel et al. 2024, "Whole-brain annotation
  and multi-connectome cell typing of Drosophila", Nature 634:139-152,
  <https://doi.org/10.1038/s41586-024-07686-5>; the optic-lobe annotations are
  from Matsliah et al. 2024, Nature 634:166-180,
  <https://doi.org/10.1038/s41586-024-07981-1>. File used:
  `Supplemental_file1_neuron_annotations.tsv` from
  <https://github.com/flyconnectome/flywire_annotations> (commit
  `a83b2776d60d5764cef36b927f5f9679c16c47a2`). **That repository declares no
  licence** (no `LICENSE` file, GitHub reports none, the README only asks users
  to cite the papers above). The cell-type names, soma positions and
  `fru_dsx` flags taken from it are redistributed inside the connectome
  artifact on the assumption that attribution suffices; see "Open licence
  question" below.
- **Synapse and neurotransmitter predictions** (used by the edge list to give
  each connection its sign): Buhmann et al. 2021, "Automatic detection of
  synaptic partners in a whole-brain Drosophila electron microscopy data set",
  Nature Methods 18:771-774, <https://doi.org/10.1038/s41592-021-01183-7>, and
  Eckstein et al. 2024, "Neurotransmitter classification from electron
  microscopy images at synaptic sites in Drosophila melanogaster", Cell
  187:2574-2594, <https://doi.org/10.1016/j.cell.2024.03.016>.

Pinned inputs of `tools/build_connectome.py` (all verified by SHA-256):

| File | Source | SHA-256 |
|---|---|---|
| `Connectivity_783.parquet` | `philshiu/Drosophila_brain_model` @ `91bdd1e7dcf193f3e7ca5a8933497fcef63b7960` | `efeb23fb99098e9c390f6869969b2a121a2ee92c833cfc45ecb2c1d8e1af0347` |
| `Completeness_783.csv` | same commit | `bbb847a4cc2caaa7a16349722d220c087317b946d148d4d592d94d250617a311` |
| `Supplemental_file1_neuron_annotations.tsv` | `flyconnectome/flywire_annotations` @ `a83b2776d60d5764cef36b927f5f9679c16c47a2` | `b214970b55d2fbe0853bba536fdcb9e28730f4eb7ab06f600491df795da683cd` |

## Neuron model

The leaky integrate-and-fire model, its parameters and the Poisson input
scheme follow Shiu et al. 2024, "A Drosophila computational brain model reveals
sensorimotor processing", Nature 634:210-219,
<https://doi.org/10.1038/s41586-024-07763-9>; code at
<https://github.com/philshiu/Drosophila_brain_model> (**MIT licence**,
Copyright (c) 2023 Philip Shiu and Nico Spiller). `app/simulator.py` is an
independent re-implementation in numba of the Brian2 model in that repository
(Brian2: <https://briansimulator.org>, CeCILL 2.1); `tests/test_brian_parity.py`
compares both when Brian2 is installed. Parameters cite Kakaria and de Bivort
2017 (membrane), Jurgensen et al. 2021 (synaptic time constant), Lazar et al.
2021 (refractory period) and Paul et al. 2015 (delay), as in the original code.

The readout populations in `tools/readouts.yaml` cite the studies that motivate
them (Zhou et al. 2015, Deutsch et al. 2019 and 2020, Clemens et al. 2018, Wang
et al. 2020 and 2021, Baker et al. 2022, von Reyn et al. 2014 and 2017, Allen
et al. 2006, Seki et al. 2011, Ishikawa et al. 2019) with DOIs.

## Software

| Package | Licence |
|---|---|
| numpy | BSD-3-Clause and others (see its `LICENSE.txt`) |
| numba, llvmlite | BSD-2-Clause; llvmlite bundles LLVM (Apache-2.0 with LLVM exception) |
| FastAPI, pydantic | MIT |
| Starlette, uvicorn, websockets, h11, click, anyio | BSD-3-Clause / MIT |
| PyYAML (builder stage only) | MIT |
| PyAV (`av`) | BSD-3-Clause; **the wheels bundle FFmpeg shared libraries** (including libavcodec, libx264 and libx265), which are LGPL-2.1+ and, because x264/x265 are enabled, GPL-2.0+ builds. Source: <https://ffmpeg.org> and <https://github.com/PyAV-Org/PyAV>. |
| pyarrow (builder stage only, not in the runtime image) | Apache-2.0 |

## Open licence question

The annotation table has no stated licence. If you publish a container image
containing the baked artifact (`connectome_auditory.npz`), consider asking the
maintainers of `flyconnectome/flywire_annotations` (Schlegel, Bates, Jefferis
et al.) to state the licence, or rebuild the artifact yourself instead of
distributing it. The FlyWire connectivity itself is CC BY 4.0 and requires
attribution, which the service exposes through `GET /v1/model` (`attribution`)
for display in the web app.

When the bundled FFmpeg (GPL-enabled build) is redistributed in an image, the
GPL source-offer obligations of FFmpeg apply to that image.
