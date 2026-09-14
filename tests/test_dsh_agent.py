import importlib.machinery
import importlib.util
from pathlib import Path
import unittest
from unittest.mock import patch, MagicMock

path = Path(__file__).resolve().parents[1] / 'linux' / 'wsl-tray-agent'
loader = importlib.machinery.SourceFileLoader('tray_agent', str(path))
spec = importlib.util.spec_from_loader(loader.name, loader)
agent = importlib.util.module_from_spec(spec)
loader.exec_module(agent)

class DshStatusTest(unittest.TestCase):
    def test_running_web_ready_and_no_proxy(self):
        response = MagicMock()
        response.__enter__.return_value.status = 200
        opener = MagicMock()
        opener.open.return_value = response
        with patch.object(agent, '_run', return_value=(0, 'LoadState=loaded\nActiveState=active\nSubState=running\nMainPID=123', '')), patch.object(agent.urllib.request, 'ProxyHandler') as proxy, patch.object(agent.urllib.request, 'build_opener', return_value=opener):
            result = agent.dsh_status()
            self.assertTrue(result['dshWebReady'])
            self.assertEqual(result['dshMainPid'], 123)
            proxy.assert_called_once_with({})
            opener.open.assert_called_once_with('http://127.0.0.1:3080/', timeout=1.0)

    def test_stopped_never_probes_or_starts(self):
        with patch.object(agent, '_run', return_value=(0, 'LoadState=loaded\nActiveState=inactive\nMainPID=0', '')) as run, patch.object(agent.urllib.request, 'build_opener') as opener:
            result = agent.dsh_status()
            self.assertFalse(result['dshWebReady'])
            opener.assert_not_called()
            self.assertEqual(run.call_args.args[0][:3], ['systemctl', 'show', 'dsh-wsl.service'])

    def test_not_found(self):
        with patch.object(agent, '_run', return_value=(1, 'LoadState=not-found\nActiveState=inactive', '')):
            self.assertEqual(agent.dsh_status()['dshLoadState'], 'not-found')

    def test_web_timeout_does_not_break_telemetry(self):
        with patch.object(agent, '_run', return_value=(0, 'LoadState=loaded\nActiveState=active', '')), patch.object(agent.urllib.request, 'build_opener', side_effect=TimeoutError):
            self.assertFalse(agent.dsh_status()['dshWebReady'])

    def test_failed_systemd_query_is_unknown(self):
        with patch.object(agent, '_run', return_value=(255, '', 'timeout')):
            result = agent.dsh_status()
            self.assertEqual(result['dshActiveState'], 'unknown')
            self.assertFalse(result['dshWebReady'])

if __name__ == '__main__':
    unittest.main()
