// Plan: /home/ben/repositories/Iverson/docs/plans/2026-10-26-csr-round12-supply-chain-implementation-plan.md, T11 S1
// Source: ci/pins.py newest, branches dockerhub-tag and maven-central, and version_key
// Replay base: a6c008dce9e4c846f8c34456ddd6297636d31979
// Re-run: dafny verify --verification-time-limit 30 --warn-contradictory-assumptions --manual-lemma-induction docs/formal/2026-10-26-csr-round12-supply-chain-implementation-plan/Pins.NewestInSeries.dfy

newtype CodePoint = x: int | 0 <= x < 0x110000
type Str = seq<CodePoint>

datatype Option<T> = None | Some(value: T)

// ---------------------------------------------------------------------------------------------------------
// Approved axioms (library boundary)
// ---------------------------------------------------------------------------------------------------------

// pattern = re.compile(re.escape(series) + r"\.(\d+)"); pattern.fullmatch(tag)          (ci/pins.py:176,180)
predicate DockerInSeries(series: Str, tag: Str)

// re.fullmatch(re.escape(series) + r"(\.\d+)+", v.text or "")                              (ci/pins.py:206)
predicate MavenInSeries(series: Str, version: Str)

// version_key: tuple(int(n) for n in re.findall(r"\d+", version))                          (ci/pins.py:138-139)
function VersionKey(version: Str): seq<int>

// ---------------------------------------------------------------------------------------------------------
// Modelled Python semantics
// ---------------------------------------------------------------------------------------------------------

// Python tuple `<` on tuples of ints: lexicographic; a proper prefix is less.
predicate KeyLess(a: seq<int>, b: seq<int>)
  decreases |a|
{
  if |a| == 0 then |b| > 0
  else if |b| == 0 then false
  else if a[0] < b[0] then true
  else if a[0] > b[0] then false
  else KeyLess(a[1..], b[1..])
}

// max(xs, key=version_key): the first element is the running maximum; a later item replaces it only when
// key(item) > key(maximum), so the first maximal element wins.
function MaxFrom(m: Str, rest: seq<Str>): Str
  decreases |rest|
{
  if |rest| == 0 then m
  else if KeyLess(VersionKey(m), VersionKey(rest[0])) then MaxFrom(rest[0], rest[1..])
  else MaxFrom(m, rest[1..])
}

function MaxByKey(xs: seq<Str>): Str
  requires |xs| > 0
{
  MaxFrom(xs[0], xs[1..])
}

// ---------------------------------------------------------------------------------------------------------
// Transliteration: newest(), branch dockerhub-tag (ci/pins.py:174-184)
// ---------------------------------------------------------------------------------------------------------

// [t["name"] for t in page["results"] if pattern.fullmatch(t["name"])]                    (ci/pins.py:180)
function DockerPageTags(series: Str, page: seq<Str>): seq<Str>
  decreases |page|
{
  if |page| == 0 then []
  else (if DockerInSeries(series, page[0]) then [page[0]] else []) + DockerPageTags(series, page[1..])
}

// while url: ... tags += ...; url = page.get("next")                                       (ci/pins.py:177-181)
function DockerTags(series: Str, pages: seq<seq<Str>>): seq<Str>
  decreases |pages|
{
  if |pages| == 0 then []
  else DockerPageTags(series, pages[0]) + DockerTags(series, pages[1..])
}

// if not tags: raise PinError(...)  ->  None;  return max(tags, key=version_key)            (ci/pins.py:182-184)
function DockerNewest(series: Str, pages: seq<seq<Str>>): Option<Str>
{
  var tags := DockerTags(series, pages);
  if |tags| == 0 then None
  else Some(MaxByKey(tags))
}

// ---------------------------------------------------------------------------------------------------------
// Transliteration: newest(), branch maven-central (ci/pins.py:201-209)
// ---------------------------------------------------------------------------------------------------------

// releases = [v.text for v in root_el.iter("version") if re.fullmatch(..., v.text or "")]  (ci/pins.py:205-206)
function MavenReleases(series: Str, versions: seq<Str>): seq<Str>
  decreases |versions|
{
  if |versions| == 0 then []
  else (if MavenInSeries(series, versions[0]) then [versions[0]] else []) + MavenReleases(series, versions[1..])
}

// if not releases: raise PinError(...)  ->  None;  return max(releases, key=version_key)     (ci/pins.py:207-209)
function MavenNewest(series: Str, versions: seq<Str>): Option<Str>
{
  var releases := MavenReleases(series, versions);
  if |releases| == 0 then None
  else Some(MaxByKey(releases))
}

// ---------------------------------------------------------------------------------------------------------
// Helper lemmas
// ---------------------------------------------------------------------------------------------------------

lemma KeyLessTrans(a: seq<int>, b: seq<int>, c: seq<int>)
  requires KeyLess(a, b) && KeyLess(b, c)
  ensures KeyLess(a, c)
  decreases |a|
{
  if |a| == 0 {
  } else {
    assert |b| > 0 && |c| > 0;
    if a[0] == b[0] && b[0] == c[0] {
      KeyLessTrans(a[1..], b[1..], c[1..]);
    }
  }
}

lemma MaxFromIn(m: Str, rest: seq<Str>)
  ensures MaxFrom(m, rest) == m || MaxFrom(m, rest) in rest
  decreases |rest|
{
  if |rest| > 0 {
    if KeyLess(VersionKey(m), VersionKey(rest[0])) {
      MaxFromIn(rest[0], rest[1..]);
    } else {
      MaxFromIn(m, rest[1..]);
    }
  }
}

lemma MaxFromGreatest(m: Str, rest: seq<Str>, y: Str)
  requires y == m || y in rest
  ensures !KeyLess(VersionKey(MaxFrom(m, rest)), VersionKey(y))
  decreases |rest|
{
  if |rest| == 0 {
    KeyLessIrrefl(VersionKey(m));
  } else {
    var r := MaxFrom(m, rest);
    if KeyLess(VersionKey(m), VersionKey(rest[0])) {
      assert r == MaxFrom(rest[0], rest[1..]);
      if y == m {
        // m < rest[0] <= r; if r < m then r < rest[0] by transitivity, contradicting rest[0]'s case.
        MaxFromGreatest(rest[0], rest[1..], rest[0]);
        if KeyLess(VersionKey(r), VersionKey(m)) {
          KeyLessTrans(VersionKey(r), VersionKey(m), VersionKey(rest[0]));
        }
      } else if y == rest[0] {
        MaxFromGreatest(rest[0], rest[1..], rest[0]);
      } else {
        assert y in rest[1..];
        MaxFromGreatest(rest[0], rest[1..], y);
      }
    } else {
      assert r == MaxFrom(m, rest[1..]);
      if y == m {
        MaxFromGreatest(m, rest[1..], m);
      } else if y == rest[0] {
        // !(m < rest[0]) and !(r < m); if r < rest[0] then ... need r >= m >= rest[0].
        MaxFromGreatest(m, rest[1..], m);
        if KeyLess(VersionKey(r), VersionKey(rest[0])) {
          KeyLessTotal(VersionKey(m), VersionKey(r));
          KeyLessTotal(VersionKey(rest[0]), VersionKey(m));
          if VersionKey(m) == VersionKey(r) {
          } else {
            KeyLessTrans(VersionKey(m), VersionKey(r), VersionKey(rest[0]));
          }
        }
      } else {
        assert y in rest[1..];
        MaxFromGreatest(m, rest[1..], y);
      }
    }
  }
}

lemma KeyLessIrrefl(a: seq<int>)
  ensures !KeyLess(a, a)
  decreases |a|
{
  if |a| > 0 {
    KeyLessIrrefl(a[1..]);
  }
}

// Totality: either a < b, b < a, or a == b.
lemma KeyLessTotal(a: seq<int>, b: seq<int>)
  ensures KeyLess(a, b) || KeyLess(b, a) || a == b
  decreases |a|
{
  if |a| > 0 && |b| > 0 && a[0] == b[0] {
    KeyLessTotal(a[1..], b[1..]);
    if a[1..] == b[1..] {
      assert a == [a[0]] + a[1..];
      assert b == [b[0]] + b[1..];
    }
  }
}

lemma MaxByKeyProps(xs: seq<Str>)
  requires |xs| > 0
  ensures MaxByKey(xs) in xs
  ensures forall y :: y in xs ==> !KeyLess(VersionKey(MaxByKey(xs)), VersionKey(y))
{
  MaxFromIn(xs[0], xs[1..]);
  assert xs == [xs[0]] + xs[1..];
  forall y | y in xs
    ensures !KeyLess(VersionKey(MaxByKey(xs)), VersionKey(y))
  {
    MaxFromGreatest(xs[0], xs[1..], y);
  }
}

lemma DockerPageTagsMem(series: Str, page: seq<Str>, t: Str)
  ensures t in DockerPageTags(series, page) <==> t in page && DockerInSeries(series, t)
  decreases |page|
{
  if |page| > 0 {
    DockerPageTagsMem(series, page[1..], t);
    assert page == [page[0]] + page[1..];
  }
}

lemma DockerTagsMem(series: Str, pages: seq<seq<Str>>, t: Str)
  ensures t in DockerTags(series, pages) <==> (exists p :: 0 <= p < |pages| && t in pages[p]) && DockerInSeries(series, t)
  decreases |pages|
{
  if |pages| > 0 {
    DockerTagsMem(series, pages[1..], t);
    DockerPageTagsMem(series, pages[0], t);
    if (exists p :: 0 <= p < |pages| && t in pages[p]) {
      var p :| 0 <= p < |pages| && t in pages[p];
      if p > 0 {
        assert t in pages[1..][p - 1];
      }
    }
    if (exists p :: 0 <= p < |pages[1..]| && t in pages[1..][p]) {
      var p :| 0 <= p < |pages[1..]| && t in pages[1..][p];
      assert t in pages[p + 1];
    }
  }
}

lemma MavenReleasesMem(series: Str, versions: seq<Str>, t: Str)
  ensures t in MavenReleases(series, versions) <==> t in versions && MavenInSeries(series, t)
  decreases |versions|
{
  if |versions| > 0 {
    MavenReleasesMem(series, versions[1..], t);
    assert versions == [versions[0]] + versions[1..];
  }
}

// ---------------------------------------------------------------------------------------------------------
// Properties
// ---------------------------------------------------------------------------------------------------------

// Citation: design PinChecks.P4_SeriesNewestStaysInSeries: "newest patch within the pinned series for `kindest/node`" (spec line 157); "`maven-central <groupId:artifactId> <series>` (the newest version in the artifact's `maven-metadata.xml` within the series)" (spec line 155).
lemma P1_ResultIsListedAndInSeries(series: Str, pages: seq<seq<Str>>, versions: seq<Str>)
  ensures DockerNewest(series, pages).Some? ==> DockerInSeries(series, DockerNewest(series, pages).value) && exists p :: 0 <= p < |pages| && DockerNewest(series, pages).value in pages[p]
  ensures MavenNewest(series, versions).Some? ==> MavenInSeries(series, MavenNewest(series, versions).value) && MavenNewest(series, versions).value in versions
{
  var tags := DockerTags(series, pages);
  if |tags| > 0 {
    MaxByKeyProps(tags);
    DockerTagsMem(series, pages, MaxByKey(tags));
  }
  var releases := MavenReleases(series, versions);
  if |releases| > 0 {
    MaxByKeyProps(releases);
    MavenReleasesMem(series, versions, MaxByKey(releases));
  }
}

// Citation: test `test_the_kindest_node_series_keeps_the_newest_patch_of_its_own_minor_across_pages` (`== "v1.35.10"`), `test_maven_central_keeps_its_series_and_ignores_pre_releases` (`newest("3.9") == "3.9.16"`).
lemma P2_NothingNewerInSeries(series: Str, pages: seq<seq<Str>>, versions: seq<Str>, t: Str)
  ensures DockerNewest(series, pages).Some? && (exists p :: 0 <= p < |pages| && t in pages[p]) && DockerInSeries(series, t) ==> !KeyLess(VersionKey(DockerNewest(series, pages).value), VersionKey(t))
  ensures MavenNewest(series, versions).Some? && t in versions && MavenInSeries(series, t) ==> !KeyLess(VersionKey(MavenNewest(series, versions).value), VersionKey(t))
{
  var tags := DockerTags(series, pages);
  DockerTagsMem(series, pages, t);
  if |tags| > 0 {
    MaxByKeyProps(tags);
  }
  var releases := MavenReleases(series, versions);
  MavenReleasesMem(series, versions, t);
  if |releases| > 0 {
    MaxByKeyProps(releases);
  }
}

// Citation: test `pytest.raises(pins.PinError, match="no org.apache.maven:apache-maven release in the 4 series")`.
lemma P3_EmptySeriesIsAnError(series: Str, pages: seq<seq<Str>>, versions: seq<Str>)
  ensures DockerNewest(series, pages).None? <==> forall p, t :: 0 <= p < |pages| && t in pages[p] ==> !DockerInSeries(series, t)
  ensures MavenNewest(series, versions).None? <==> forall t :: t in versions ==> !MavenInSeries(series, t)
{
  var tags := DockerTags(series, pages);
  if |tags| > 0 {
    DockerTagsMem(series, pages, tags[0]);
  } else {
    forall p, t | 0 <= p < |pages| && t in pages[p]
      ensures !DockerInSeries(series, t)
    {
      DockerTagsMem(series, pages, t);
    }
  }
  var releases := MavenReleases(series, versions);
  if |releases| > 0 {
    MavenReleasesMem(series, versions, releases[0]);
  } else {
    forall t | t in versions
      ensures !MavenInSeries(series, t)
    {
      MavenReleasesMem(series, versions, t);
    }
  }
}
