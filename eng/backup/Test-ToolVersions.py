"""Synthetic negative controls for exact backup tool versions."""

from tool_versions import POSTGRES_TOOLS, matches


def main():
    for name in sorted(POSTGRES_TOOLS):
        expected = f"{name} (PostgreSQL) 18.6"
        assert matches(name, expected, expected)
        assert matches(name, expected, expected + " (Ubuntu 18.6-1.pgdg24.04+2)")
        assert matches(name, expected, expected + " (Debian 18.6-1.pgdg13+1)")
        for bad in (
            f"{name} (PostgreSQL) 18.5",
            f"{name} (PostgreSQL) 18.60",
            expected + " arbitrary-suffix",
            expected + " (Ubuntu 18.7-1)",
        ):
            assert not matches(name, expected, bad)
    assert matches("age", "v1.3.2", "v1.3.2\n")
    assert not matches("age", "v1.3.2", "v1.3.20")
    assert not matches("age", "v1.3.2", "v1.3.1")
    assert not matches("age", "v1.3.2", None)
    print("Backup tool version boundaries passed.")


if __name__ == "__main__":
    main()
