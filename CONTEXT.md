# OES Leak Monitor

An actinometry-based air-leak detector for a single plasma chamber: it watches emission
lines in the chamber's own light, judges them against a leak-free Golden Run, and reports
what it finds to the operator and to the fab host.

This glossary is the definition of record for the project's own words. Terms are listed
only where an agent reading the code would otherwise get them wrong — either because the
word means something narrower here than in general use, or because two things in this
repo answer to the same word.

## Leak detection

**Ratio**:
One configured entry in the set of things being watched — a signal line, the way it is
extracted, its thresholds, and (only where it divides) a reference line. Named for the
usual case, not a guarantee: an entry set to absolute intensity divides by nothing and
its reference line takes no part in the reading.

**Monitored value**:
The single number a ratio produces from each frame, and the thing every judgement is
actually made against. What it means depends on the entry's mode, so it is the subject of
every statement about units, pedestals and comparability.

**Signal line / reference line**:
Within a ratio, the line being asked about and the line it is divided by. A reference line
exists to cancel drift in plasma conditions; where an entry does not divide, it has none.

**Golden Run**:
A named, stored record of what every ratio reads with no leak present, captured over a
window and kept with the conditions it was captured under. It is the thing today's
readings mean something relative to; the tool has no absolute scale of its own. It
describes **one** steady operating point, and one of them is active at a time — a recipe
with several needs several, switched by hand. See `docs/adr/0001`.

**Recipe / run / step**:
A recipe is the named process program; a run is one execution of it; a step is a stretch of
a run with its own setpoints. A Golden Run is named for the recipe but describes only one
step of it.

**Steady segment**:
The part of a step where the operating point actually holds still — the only thing a
baseline can be taken from. A soft start's first seconds belong to the step but not to its
steady segment, which is why the segment sometimes has to be chosen after the fact, from
the recorded trace, rather than by standing at the tool for a minute.

**Baseline build**:
Producing a Golden Run from recordings already on disk instead of from live frames. It
exists for the runs a live capture cannot reach: one that ramps before it settles, and one
too short to fill a capture window on its own, several of which are pooled into one
baseline.

**Golden Run baseline**:
One ratio's share of a Golden Run — its mean and scatter. A ratio can lack one while
others have theirs, and it is then unjudgeable rather than normal. It is asked to be two
things at once: the reference point a reading is compared against, and the divisor that
every percentage, threshold factor and leak-rate fit divides by. Only the second needs a
mean clear of zero — which is why a baseline can be refused for a line that is behaving
exactly as it should.

**Absent-line baseline**:
A Golden Run baseline whose leak-free value is the noise floor, because the species being
watched is not in the chamber unless there is a leak. It is this tool's ordinary condition
rather than a failed capture: what is being detected is the line's appearance, not its
change. Such a quantity cannot be judged as a fraction of its own mean, so it belongs in
σ units.

**Reactant-gas regime**:
A recipe that deliberately admits the species being watched. The line is then present with
no leak, so its presence is not evidence of one, and a leak shows only as a small increment
on a large signal.

**Plasma gate**:
The test for whether a frame is worth judging at all, phrased as "would the recorder be
saving this frame". Deliberately the same measurement the logger's trigger makes, so the
gate and the recorder can never disagree about whether the plasma is on.

**Pedestal**:
The part of a reading that is continuum rather than line — present only when a monitored
quantity subtracts nothing. A quantity carrying one cannot be judged as a fraction of its
Golden Run baseline, which is why it is the single predicate that switches thresholds, display,
slope and calibration into σ units.

**Low signal**:
The condition of a monitored entry whose line is too close to the noise to be believed.
It is held out of the composite judgement rather than reported as normal — an unreadable
entry is not a passing one.

**Latched**:
Of an alarm: still asserted after the condition that raised it has gone. Only a human
acknowledgement clears it, and that clearing is always attributable to a named user.

**Sustained confirmation**:
The requirement that a threshold stay crossed for a stated number of seconds before the
state changes. It is what separates a leak from a spike.

**Composite level**:
The single Normal / Warning / Alarm verdict for the whole tool, formed from the
individual entries' states. Entries that are unreadable take no part in it.

**Acknowledge**:
The act of a named person ending a latched alarm. It is the only thing that clears a
latch, so an alarm that ends without one ended by itself — which is a fact about the tool,
not about the chamber.

**Dropout**:
A brief closure of the plasma gate with good frames either side of it: an instrument
fault, and counted as one. The plasma genuinely going off is longer and is not a dropout.

**Factory ratio set**:
The ratios the tool ships with. Factory here means what one machine's known-leak
measurement actually selected, not what the textbook would suggest — several of the
obvious choices were tried on that machine and did not move.

**Acquisition fingerprint**:
The set of hardware settings a measurement was taken under. Two readings taken under
different fingerprints are not comparable, whatever their numbers say.

## Quantitative leak rate

**Leak rate**:
The estimated size of the leak in mbar·L/s. It exists only where the tool has been shown
a leak of known size; without that it has an opinion about direction, not magnitude.

**Calibrated leak**:
A physical leak element of known rate, admitted deliberately so the tool can learn the
relationship between what it sees and what is actually leaking. It must be the same gas
the tool is meant to detect.

**Calibration point**:
One observation pairing a known leak rate with the rise each monitored entry showed
under it.

**Sensitivity**:
Per entry, how much that entry moves per unit of leak. Inverting it is what turns an
observation back into a leak rate.

## Emission lines

**Continuum**:
The broad background under a line, estimated by interpolating between two windows placed
either side of it. Two of the three extraction modes subtract it; the third deliberately
does not.
_Avoid_: baseline on its own — it means both this and a Golden Run baseline. Always
qualify it: continuum baseline, or Golden Run baseline.

**SNR**:
A line's height against the local continuum noise, used here as a gate rather than as a
figure of merit. A reading that subtracts no continuum has no SNR at all — unknown, which
is not the same as high.

**Extraction mode**:
How a number is pulled out of the spectrum at a wavelength: peak height, integrated area,
or the raw mean with nothing subtracted. Two readings taken in different modes are in
different units and are not comparable.

**Correction (wavelength)**:
An additive offset applied to a catalog line's wavelength to follow axis drift. It is a
property of the line, so correcting it once re-aligns every place that line is used.

**User line**:
A site's own emission line, layered over the fixed built-in catalog. It always carries a
marked species name so that a label naming one can never be mistaken for a built-in.

## Recorded data

**Recording**:
One written CSV, read back. It is a file, not an episode: a long save is several
recordings, and reading one back never implies reading the whole episode.

**Save session**:
The episode itself — the stretch from the recorder opening a file to it closing one,
which may span several recordings as they rotate.
_Avoid_: run on its own. It means a Golden Run, a plasma process, or a launch of the app,
depending on who is speaking. Say which.

**Archive**:
A day's recordings compressed into a single file in place. Ageing data is archived, never
deleted — the retention decision belongs to whoever owns the data, not to this tool, and
an archived day stays fully readable.

**Trigger threshold**:
The intensity above which the recorder considers something to be happening and starts
writing. The same number decides whether the plasma gate is open, so it is never only a
recording setting.

## Measuring, and not measuring

**Test mode**:
The state in which spectra are synthetic rather than measured. It is entered on purpose
when there is no hardware, and also as a fallback when hardware fails to load — which is
why it is stated on screen and in the log rather than inferred.

**Test-mode fallback**:
A connect that succeeded, reported healthy, and is nonetheless producing synthetic
spectra. The most dangerous state the tool has, because everything downstream of it looks
normal.

**Replay**:
Feeding a previously recorded spectrum file back through the live pipeline to judge the
detection itself rather than the chamber. Test mode is part of its meaning, not a
precondition bolted on: measured frames are never replaced, and anything a replay writes
is marked as such so it can never be read back as measurement.
_Avoid_: simulation. It named a removed predecessor that resampled recordings onto the
synthetic axis, and survives only as a settings key that now points at a replay file.

## Talking to the fab host

**Chamber code**:
The two digits identifying which chamber this instance speaks for, stamped into every id
the host sees. The unstamped value is not a chamber — it is the mark of a profile that
has not been configured yet.

**Stamping**:
Rewriting a site profile's ids with the chamber code at start-up, and refusing to start
if the result would claim ids belonging to another kind of sensor.

**Effective profile**:
The stamped copy that is actually served to the host, kept apart from the site-editable
original so that the file a person edits is never the file the host is answered from.

## Applying a change

**Staged**:
Of a setting: edited and saved now, taking effect only when acquisition next restarts.
The measurement running right now was defined by the values it started with.

**Hot-applied**:
Of a setting: taking effect on the next frame. Used where a staged value would leave two
parts of the system disagreeing for the rest of the run.
