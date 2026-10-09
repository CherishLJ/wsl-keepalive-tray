import importlib.machinery
import importlib.util
from pathlib import Path
import unittest
import urllib.error
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
        win = {'dshWinClientUp': True, 'dshWinWebReady': True, 'dshWinCheckedUnixMs': 1}
        with patch.object(agent, '_run', return_value=(0, 'LoadState=loaded\nActiveState=active\nSubState=running\nMainPID=123', '')), \
             patch.object(agent, 'dsh_win_status', return_value=win), \
             patch.object(agent.urllib.request, 'ProxyHandler') as proxy, \
             patch.object(agent.urllib.request, 'build_opener', return_value=opener):
            result = agent.dsh_status()
            self.assertTrue(result['dshWebReady'])
            self.assertEqual(result['dshMainPid'], 123)
            self.assertTrue(result['dshWinWebReady'])
            proxy.assert_called_once_with({})
            opener.open.assert_called_once_with('http://127.0.0.1:3080/', timeout=1.0)

    def test_auth_gate_401_is_web_reachable(self):
        error = urllib.error.HTTPError('http://127.0.0.1:3080/', 401, 'Unauthorized', hdrs=None, fp=None)
        opener = MagicMock()
        opener.open.side_effect = error
        win = {'dshWinClientUp': False, 'dshWinWebReady': False, 'dshWinCheckedUnixMs': 1}
        with patch.object(agent, '_run', return_value=(0, 'LoadState=loaded\nActiveState=active\nSubState=running\nMainPID=9', '')), \
             patch.object(agent, 'dsh_win_status', return_value=win), \
             patch.object(agent.urllib.request, 'build_opener', return_value=opener):
            result = agent.dsh_status()
            self.assertTrue(result['dshWebReady'])
            self.assertEqual(result['dshWebDetail'], 'HTTP 401')
            self.assertEqual(result['dshError'], '')

    def test_server_error_is_not_web_ready(self):
        error = urllib.error.HTTPError('http://127.0.0.1:3080/', 502, 'Bad Gateway', hdrs=None, fp=None)
        opener = MagicMock()
        opener.open.side_effect = error
        with patch.object(agent, '_run', return_value=(0, 'LoadState=loaded\nActiveState=active', '')), \
             patch.object(agent, 'dsh_win_status', return_value={'dshWinCheckedUnixMs': 1}), \
             patch.object(agent.urllib.request, 'build_opener', return_value=opener):
            result = agent.dsh_status()
            self.assertFalse(result['dshWebReady'])
            self.assertIn('HTTP 502', result['dshError'])

    def test_win_client_and_web_are_independent(self):
        def probe(url, timeout):
            if url.endswith(':19387/'):
                return True, 'HTTP 401'
            return False, '未响应'
        with patch.object(agent, '_systemctl_user', return_value=(0, 'LoadState=loaded\nActiveState=active\nSubState=running\nMainPID=4', '')), \
             patch.object(agent, '_probe_http', side_effect=probe):
            result = agent.dsh_win_status()
            self.assertTrue(result['dshWinClientUp'])
            self.assertFalse(result['dshWinWebReady'])
            self.assertIn('网页入口未响应', result['dshWinError'])

    def test_win_client_down_does_not_fail_a_live_web_entry(self):
        def probe(url, timeout):
            if url.endswith(':19388/'):
                return True, 'HTTP 200'
            return False, '未响应'
        with patch.object(agent, '_systemctl_user', return_value=(0, 'LoadState=loaded\nActiveState=active\nSubState=running\nMainPID=4', '')), \
             patch.object(agent, '_probe_http', side_effect=probe):
            result = agent.dsh_win_status()
            self.assertFalse(result['dshWinClientUp'])
            self.assertTrue(result['dshWinWebReady'])
            self.assertEqual(result['dshWinError'], '')

    def test_stopped_never_probes_or_starts(self):
        with patch.object(agent, '_run', return_value=(0, 'LoadState=loaded\nActiveState=inactive\nMainPID=0', '')) as run, \
             patch.object(agent, 'dsh_win_status', return_value={'dshWinCheckedUnixMs': 1}) as win, \
             patch.object(agent.urllib.request, 'build_opener') as opener:
            result = agent.dsh_status()
            self.assertFalse(result['dshWebReady'])
            opener.assert_not_called()
            win.assert_called_once_with()
            self.assertEqual(run.call_args.args[0][:3], ['systemctl', 'show', 'dsh-wsl.service'])

    def test_not_found(self):
        with patch.object(agent, '_run', return_value=(1, 'LoadState=not-found\nActiveState=inactive', '')):
            self.assertEqual(agent.dsh_status()['dshLoadState'], 'not-found')

    def test_web_timeout_does_not_break_telemetry(self):
        with patch.object(agent, '_run', return_value=(0, 'LoadState=loaded\nActiveState=active', '')), \
             patch.object(agent, 'dsh_win_status', return_value={'dshWinCheckedUnixMs': 1}), \
             patch.object(agent.urllib.request, 'build_opener', side_effect=TimeoutError):
            self.assertFalse(agent.dsh_status()['dshWebReady'])

    def test_failed_systemd_query_is_unknown(self):
        with patch.object(agent, '_run', return_value=(255, '', 'timeout')), \
             patch.object(agent, 'dsh_win_status', return_value={'dshWinCheckedUnixMs': 1}):
            result = agent.dsh_status()
            self.assertEqual(result['dshActiveState'], 'unknown')
            self.assertFalse(result['dshWebReady'])

if __name__ == '__main__':
    unittest.main()
