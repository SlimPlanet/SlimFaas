import argparse
import http.client
import unittest

from narrate import speech_server


class SpeechServerTests(unittest.TestCase):
    def test_documented_local_endpoints(self):
        for scheme, host, expected, connection_type in [
            ("http", "localhost", "127.0.0.1", http.client.HTTPConnection),
            ("https", "[::1]", "::1", http.client.HTTPSConnection),
        ]:
            with self.subTest(host=host):
                connection = speech_server(f"{scheme}://{host}:8880/v1/audio/speech")
                self.assertIsInstance(connection, connection_type)
                self.assertEqual((connection.host, connection.port), (expected, 8880))
                connection.close()

    def test_rejects_nonlocal_or_unexpected_destination(self):
        for value in ["https://example.org/v1/audio/speech", "https://localhost.example.org/v1/audio/speech",
                      "https://localhost/other", "https://user@localhost/v1/audio/speech",
                      "https://localhost/v1/audio/speech?redirect=1", "file:///v1/audio/speech"]:
            with self.subTest(value=value), self.assertRaises(argparse.ArgumentTypeError):
                speech_server(value)


if __name__ == "__main__":
    unittest.main()
