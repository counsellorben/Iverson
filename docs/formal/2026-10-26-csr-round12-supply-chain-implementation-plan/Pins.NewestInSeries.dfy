// Plan: /home/ben/repositories/Iverson/docs/plans/2026-10-26-csr-round12-supply-chain-implementation-plan.md, T11 S1
// Source: ci/pins.py newest, branches dockerhub-tag and maven-central, and version_key
// Replay base: 680eb9cf5a6dc2d3dd6374446822969441bfed0f
// Re-run: dafny verify --verification-time-limit 30 --warn-contradictory-assumptions --manual-lemma-induction docs/formal/2026-10-26-csr-round12-supply-chain-implementation-plan/Pins.NewestInSeries.dfy

// ---- Strings (Python str: code points) -------------------------------------------------------------------

newtype CodePoint = x: int | 0 <= x < 0x110000
type Str = seq<CodePoint>

datatype Option<T> = None | Some(value: T)

// ---- Approved axioms (library boundary) ------------------------------------------------------------------

// re.compile(re.escape(series) + r"\.(\d+)").fullmatch(tag)
predicate DockerInSeries(series: Str, tag: Str)

// re.fullmatch(re.escape(series) + r"(\.\d+)+", version)
predicate MavenInSeries(series: Str, version: Str)

// version_key: tuple(int(n) for n in re.findall(r"\d+", version))
function VersionKey(version: Str): seq<int>

// ---- Modelled library: Python tuple `<` on tuples of int ---------------------------------------------------

predicate KeyLess(a: seq<int>, b: seq<int>)
  decreases |a|
{
  if |a| == 0 then |b| > 0
  else if |b| == 0 then false
  else a[0] < b[0] || (a[0] == b[0] && KeyLess(a[1..], b[1..]))
}

// ---- Modelled library: max(xs, key=version_key) -- first maximal element ---------------------------------
// CPython's max keeps the current item and replaces it only when key(item) > maxkey, i.e. KeyLess(maxkey, key(item)).

function MaxByKeyFrom(best: Str, rest: seq<Str>): Str
  decreases |rest|
{
  if |rest| == 0 then best
  else MaxByKeyFrom(if KeyLess(VersionKey(best), VersionKey(rest[0])) then rest[0] else best, rest[1..])
}

function MaxByKey(xs: seq<Str>): Str
  requires |xs| > 0
{
  MaxByKeyFrom(xs[0], xs[1..])
}

// ---- Transliteration: newest, branch dockerhub-tag ---------------------------------------------------------
// tags += [t["name"] for t in page["results"] if pattern.fullmatch(t["name"])], over pages in order.

function DockerPageTags(series: Str, page: seq<Str>): seq<Str>
  decreases |page|
{
  if |page| == 0 then []
  else (if DockerInSeries(series, page[0]) then [page[0]] else []) + DockerPageTags(series, page[1..])
}

function DockerTags(series: Str, pages: seq<seq<Str>>): seq<Str>
  decreases |pages|
{
  if |pages| == 0 then []
  else DockerPageTags(series, pages[0]) + DockerTags(series, pages[1..])
}

// None models `raise PinError(f"Docker Hub lists no {repo} tag in the {series} series")`.
function DockerNewest(series: Str, pages: seq<seq<Str>>): Option<Str>
{
  var tags := DockerTags(series, pages);
  if |tags| == 0 then None
  else Some(MaxByKey(tags))
}

// ---- Transliteration: newest, branch maven-central ---------------------------------------------------------
// releases = [v.text for v in root_el.iter("version") if re.fullmatch(..., v.text or "")]

function MavenReleases(series: Str, versions: seq<Str>): seq<Str>
  decreases |versions|
{
  if |versions| == 0 then []
  else (if MavenInSeries(series, versions[0]) then [versions[0]] else []) + MavenReleases(series, versions[1..])
}

// None models `raise PinError(f"Maven Central lists no {coordinates} release in the {series} series")`.
function MavenNewest(series: Str, versions: seq<Str>): Option<Str>
{
  var releases := MavenReleases(series, versions);
  if |releases| == 0 then None
  else Some(MaxByKey(releases))
}

// ---- Helper lemmas -----------------------------------------------------------------------------------------

lemma KeyLessTrans(a: seq<int>, b: seq<int>, c: seq<int>)
  requires KeyLess(a, b) && KeyLess(b, c)
  ensures KeyLess(a, c)
  decreases |a|
{
  if |a| > 0 && |b| > 0 && |c| > 0 && a[0] == b[0] && b[0] == c[0] {
    KeyLessTrans(a[1..], b[1..], c[1..]);
  }
}

lemma KeyLessTotal(a: seq<int>, b: seq<int>)
  ensures KeyLess(a, b) || a == b || KeyLess(b, a)
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

lemma KeyLessNegTrans(a: seq<int>, b: seq<int>, c: seq<int>)
  requires KeyLess(a, c) && !KeyLess(a, b)
  ensures KeyLess(b, c)
{
  KeyLessTotal(a, b);
  if a != b {
    KeyLessTrans(b, a, c);
  }
}

lemma MaxByKeyFromProps(best: Str, rest: seq<Str>)
  ensures MaxByKeyFrom(best, rest) == best || MaxByKeyFrom(best, rest) in rest
  ensures !KeyLess(VersionKey(MaxByKeyFrom(best, rest)), VersionKey(best))
  ensures forall t :: t in rest ==> !KeyLess(VersionKey(MaxByKeyFrom(best, rest)), VersionKey(t))
  decreases |rest|
{
  var r := MaxByKeyFrom(best, rest);
  if |rest| == 0 {
    KeyLessTotal(VersionKey(best), VersionKey(best));
    if KeyLess(VersionKey(best), VersionKey(best)) {
      KeyLessIrrefl(VersionKey(best));
    }
  } else {
    var upd := KeyLess(VersionKey(best), VersionKey(rest[0]));
    var nb := if upd then rest[0] else best;
    MaxByKeyFromProps(nb, rest[1..]);
    assert r == MaxByKeyFrom(nb, rest[1..]);
    // r is best, or in rest
    assert r == nb || r in rest[1..];
    assert forall t :: t in rest[1..] ==> t in rest;
    // r is not below best
    if KeyLess(VersionKey(r), VersionKey(best)) {
      if upd {
        KeyLessTrans(VersionKey(r), VersionKey(best), VersionKey(nb));
      }
      assert false;
    }
    // r is not below rest[0]
    if KeyLess(VersionKey(r), VersionKey(rest[0])) {
      if !upd {
        KeyLessNegTrans(VersionKey(r), VersionKey(best), VersionKey(rest[0]));
      }
      assert false;
    }
    forall t | t in rest
      ensures !KeyLess(VersionKey(r), VersionKey(t))
    {
      if t != rest[0] {
        assert t in rest[1..];
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

lemma MaxByKeyProps(xs: seq<Str>)
  requires |xs| > 0
  ensures MaxByKey(xs) in xs
  ensures forall t :: t in xs ==> !KeyLess(VersionKey(MaxByKey(xs)), VersionKey(t))
{
  MaxByKeyFromProps(xs[0], xs[1..]);
  assert xs == [xs[0]] + xs[1..];
  forall t | t in xs
    ensures !KeyLess(VersionKey(MaxByKey(xs)), VersionKey(t))
  {
    if t != xs[0] {
      assert t in xs[1..];
    }
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
    if (exists q :: 0 <= q < |pages[1..]| && t in pages[1..][q]) {
      var q :| 0 <= q < |pages[1..]| && t in pages[1..][q];
      assert t in pages[q + 1];
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

// ---- Properties --------------------------------------------------------------------------------------------

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
  if |tags| > 0 {
    MaxByKeyProps(tags);
    DockerTagsMem(series, pages, t);
  }
  var releases := MavenReleases(series, versions);
  if |releases| > 0 {
    MaxByKeyProps(releases);
    MavenReleasesMem(series, versions, t);
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
    assert tags[0] in tags;
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
    assert releases[0] in releases;
  } else {
    forall t | t in versions
      ensures !MavenInSeries(series, t)
    {
      MavenReleasesMem(series, versions, t);
    }
  }
}
