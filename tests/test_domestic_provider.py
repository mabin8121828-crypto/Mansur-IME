# Copyright (c) 2026 Mansur
# SPDX-License-Identifier: MIT
"""Official-host parameter adaptation; fixed loopback fixtures, no accounts."""
from types import SimpleNamespace
from urllib.parse import urlsplit
import sys
from pathlib import Path
import unittest
sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'learning'))
import api_provider
import worker
import test_api_provider as fixtures
from test_openrouter_provider import respond

class DomesticParameters(unittest.TestCase):
    def payload(self, host, model, study=False):
        provider = object.__new__(api_provider.CompatibleProvider)
        provider.model, provider.url, provider.partial = model, urlsplit('https://' + host + '/v1'), lambda value: None
        job = worker.Job(1, '你好', 'af_heart', 1)
        if study:
            job.operation = 'selection_study'; job.study_messages = [{'role':'user','content':'Hello'}]
        return provider.payload(job)

    def test_kimi_model_constraints(self):
        fast = self.payload('api.moonshot.cn', 'kimi-k2.6')
        self.assertNotIn('temperature', fast); self.assertEqual(fast['thinking'], {'type':'disabled'})
        flagship = self.payload('api.moonshot.cn', 'kimi-k3')
        self.assertNotIn('thinking', flagship); self.assertNotIn('temperature', flagship)
        self.assertEqual(flagship['reasoning_effort'], 'low'); self.assertEqual(flagship['max_tokens'], 4096)
        coding = self.payload('api.moonshot.cn', 'kimi-k2.7-code-highspeed')
        self.assertNotIn('thinking', coding); self.assertNotIn('temperature', coding)

    def test_qwen_hybrid_and_mandatory_thinking_only_on_official_hosts(self):
        for host in ('dashscope.aliyuncs.com', 'tenant.cn-beijing.maas.aliyuncs.com'):
            for model in ('qwen-plus','qwen-flash','qwen3.8-flash','qwen3.7-plus-2026-05-26','qwen3-32b'):
                value=self.payload(host, model)
                self.assertIs(value['enable_thinking'],False)
                study=self.payload(host, model,study=True)
                self.assertFalse(study['stream']); self.assertIs(study['enable_thinking'],False)
            for model in ('qwen3.8-2.4t-a95b','qwen3.7-max-preview','qwen3-235b-a22b-thinking-2507'):
                value=self.payload(host,model)
                self.assertNotIn('enable_thinking',value); self.assertEqual(value['max_tokens'],4096)
        self.assertNotIn('enable_thinking',self.payload('maas.aliyuncs.com.example.org','qwen3.8-flash'))

    def test_glm_optional_and_mandatory_thinking(self):
        regular = self.payload('open.bigmodel.cn', 'glm-5.2')
        self.assertEqual(regular['thinking'], {'type':'disabled'})
        mandatory = self.payload('open.bigmodel.cn', 'glm-5.3-flash')
        self.assertNotIn('thinking', mandatory); self.assertNotIn('temperature', mandatory)
        self.assertEqual(mandatory['max_tokens'], 4096)

    def test_minimax_separates_reasoning_and_preserves_single_owned_input(self):
        for host in ('api.minimax.cn','api.minimaxi.com','api.minimax.io'):
            value = self.payload(host, 'MiniMax-M3')
            self.assertTrue(value['reasoning_split']); self.assertEqual(value['thinking'], {'type':'disabled'})
            self.assertEqual(value['messages'][-1]['content'], '你好'); self.assertEqual(len(value['messages']), 2)
            study = self.payload(host, 'MiniMax-M2.7', study=True)
            self.assertFalse(study['stream']); self.assertNotIn('thinking', study)
            self.assertEqual(study['max_tokens'],4096)

    def test_custom_and_other_services_do_not_receive_vendor_options(self):
        for model in ('kimi-k3','MiniMax-M3','glm-5.3-flash'):
            value = self.payload('example.org',model)
            self.assertEqual(value['temperature'],0); self.assertEqual(value['max_tokens'],512)
            self.assertNotIn('thinking',value); self.assertNotIn('reasoning_split',value)

class DomesticTransport(unittest.TestCase):
    create = fixtures.CompatibleApiTests.create
    def test_stepfun_binary_wav_and_requested_sample_rate(self):
        # Fixed local response at the requested rate, no speaker is opened.
        provider, server = self.create(lambda h,s: respond(h, raw=fixtures.wav_bytes(rate=16000), extra={'Content-Type':'audio/wav'}), speech=True, speech_api_service_id='speech-stepfun', speech_api_sample_rate=16000)
        chunks=[]; provider.synthesize(worker.Job(1,'Hello','af_heart',.75),'Hello',lambda pcm,rate: chunks.append((len(pcm),rate)))
        self.assertTrue(chunks); self.assertEqual(chunks[0][1],16000)
        call=server.calls[0]; self.assertEqual(call['path'],'/custom/v1/audio/speech')
        self.assertEqual(call['payload']['sample_rate'],16000); self.assertEqual(call['payload']['speed'],.75)

if __name__ == '__main__': unittest.main()
