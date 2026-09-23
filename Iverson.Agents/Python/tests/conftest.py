"""Guard for the `tests` package name.

`iverson_client` is imported by path (the `.pth` line), and that path carries its own regular
package named `tests`. pytest's prepend import mode puts this directory's parent at sys.path[0],
so `tests.<module>` resolves here — but only while that ordering holds. If it ever stops holding,
`from tests.test_schema import policy_doc_type` would silently import the client's package (or
fail with a misleading ImportError), so this conftest fails collection with the real cause."""
from pathlib import Path

import tests

_here = Path(__file__).resolve().parent
_resolved = Path(tests.__file__).resolve().parent
if _resolved != _here:
    raise ImportError(
        f"the `tests` package resolved to {_resolved}, not {_here}: another `tests` package on "
        "sys.path (the iverson_client .pth path carries one) shadows the agent's; run pytest "
        "from Iverson.Agents/Python with the default prepend import mode")
