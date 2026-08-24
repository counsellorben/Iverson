"""Tests for the Python conformance driver's own reporting code (``conformance/driver.py``), as
distinct from the ``iverson_client`` library it drives. ``entity_to_dict`` is the boundary the
harness's IVC-LIFE-008 assertion reads through: the library hydrates a relation onto its instance
via ``setattr`` (deliberately not a declared annotated field — see
``iverson_client.core._hydrate_relations`` and spec assumption A22), and this driver has to notice
that and report it, or the harness sees no hydration even though the caller already holds it.

This guards the fix in Iverson-conformance's client-conformance-harness branch, Task 6 fix round 1:
before that fix, ``entity_to_dict`` walked only ``__annotations__`` and silently dropped every
hydrated relation. These tests are the regression guard fix round 2 asked for — they fail loudly if
that gap reopens, rather than relying on the live matrix (which is not run per-commit) to notice.
"""
from __future__ import annotations

import grpc

from conformance.driver import _DriverSchemaCatalogClient, entity_to_dict
from conformance.models import PyArticle, PyAuthor, PyTag
from iverson_client.core import IversonClient


def _make_author(name: str) -> PyAuthor:
    author = PyAuthor()
    author.id = "01a016fa-ed06-7cd7-ad36-63042f0f94a8"
    author.tenant_id = "tenant_bypass"
    author.owner_id = "owner-1"
    author.name = name
    return author


def _make_tag(label: str) -> PyTag:
    tag = PyTag()
    tag.id = "01a016fa-ed26-7ece-ac18-1794837d1c68"
    tag.tenant_id = "tenant_bypass"
    tag.owner_id = "owner-1"
    tag.label = label
    return tag


def _make_article() -> PyArticle:
    article = PyArticle()
    article.id = "01a016fa-ed3d-72b3-b527-f1cfc1122a9c"
    article.tenant_id = "tenant_bypass"
    article.owner_id = "owner-1"
    article.title = "title"
    article.py_author_id = "01a016fa-ed06-7cd7-ad36-63042f0f94a8"
    article.py_tag_ids = ["01a016fa-ed26-7ece-ac18-1794837d1c68"]
    article.py_tag_id = "01a016fa-ed26-7ece-ac18-1794837d1c68"
    return article


def test_hydrated_many_to_one_nav_member_is_reported_with_its_own_key():
    """many_to_one: the library derives ``py_author`` from ``py_author_id`` (strip ``_id``) and
    sets it via setattr — the same mechanism ``iverson_client.core._hydrate_relations`` uses. The
    driver must report it, carrying the child's own ``id``, not merely the foreign key."""
    article = _make_article()
    article.py_author = _make_author("Ada")

    out = entity_to_dict(article)

    assert "py_author" in out
    assert out["py_author"]["id"] == "01a016fa-ed06-7cd7-ad36-63042f0f94a8"
    assert out["py_author"]["name"] == "Ada"


def test_hydrated_many_to_many_nav_member_is_reported_with_its_own_key():
    """many_to_many: the library derives the PLURAL ``py_tags`` from ``py_tag_ids`` (strip
    ``_ids``, append ``s``) — distinct derivation from many_to_one's singular strip, and this is
    the case that most directly exercises it."""
    article = _make_article()
    article.py_tags = [_make_tag("news")]

    out = entity_to_dict(article)

    assert "py_tags" in out
    assert isinstance(out["py_tags"], list)
    assert out["py_tags"][0]["id"] == "01a016fa-ed26-7ece-ac18-1794837d1c68"
    assert out["py_tags"][0]["label"] == "news"


def test_non_hydrated_entity_reports_no_nav_member():
    """Anti-gaming assertion: an article that was never hydrated (the ordinary depth-0 case, or a
    depth-1 read whose relation the server didn't hydrate) must report NEITHER ``py_author`` NOR
    ``py_tags`` — nothing set them via setattr, so ``hasattr`` is false and the driver must not
    invent them. This is what proves entity_to_dict reports only what the library actually
    produced, not a client-side simulation of hydration."""
    article = _make_article()

    out = entity_to_dict(article)

    assert "py_author" not in out
    assert "py_tags" not in out
    # the foreign keys themselves are still reported as ordinary declared fields
    assert out["py_author_id"] == "01a016fa-ed06-7cd7-ad36-63042f0f94a8"
    assert out["py_tag_ids"] == ["01a016fa-ed26-7ece-ac18-1794837d1c68"]


# ── _DriverSchemaCatalogClient's attribute-set invariant (Task 8 fix round 1) ──────────────────


def test_driver_schema_catalog_client_reproduces_every_attribute_the_base_constructor_sets():
    """Pins the invariant ``_DriverSchemaCatalogClient`` reproduces by hand.

    The subclass exists because ``IversonClient.__init__`` builds its own channel from an
    ``IversonClientCredentials``, which can carry neither the Host header Authentik stamps the
    token's ``iss`` from nor a scope — so the conformance driver has to hand it the dual-identity
    channel the driver already built. To do that it sets, by hand, exactly the attributes the base
    constructor sets, and inherits ``get_schema`` unmodified.

    That hand-reproduction has no mechanical guard of its own. The day the base constructor gains a
    fourth attribute that ``get_schema`` or ``_acting_user_metadata`` reads, the driver either
    raises ``AttributeError`` — which the harness would report as a Python IVC-SCH-001 conformance
    FAIL for what is actually a driver bug — or silently falls back to a stale default and reports a
    GREEN cell for behaviour the real Python client would never produce. Both are failures that
    surface in the wrong place, days later, as a client defect.

    This test moves that failure into Python's own suite, where it reads as what it is. It compares
    instance attribute sets rather than a hardcoded list, so it needs no maintenance when the
    subclass is correctly updated — only when it is not.
    """
    # localhost:1 is never dialed: grpc channels connect lazily and no RPC is issued here.
    base = IversonClient(host="localhost", port=1)
    driver_channel = grpc.insecure_channel("localhost:1")
    try:
        driver_client = _DriverSchemaCatalogClient(driver_channel)
        missing = sorted(set(vars(base)) - set(vars(driver_client)))
        assert not missing, (
            "IversonClient.__init__ sets attribute(s) that _DriverSchemaCatalogClient does not "
            f"reproduce: {missing}. The conformance driver inherits get_schema from IversonClient, "
            "so any attribute the base sets and get_schema (or _acting_user_metadata) reads must be "
            "set by the subclass too — see conformance/driver.py."
        )
    finally:
        driver_channel.close()
        base.close()
