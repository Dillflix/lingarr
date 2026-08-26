<template>
    <CardComponent title="Reference-free source-unit benchmark">
        <template #description>
            Benchmark source-boundary models against the heuristic baseline using exact source cue windows captured
            during live translation. This stage is entirely source-language, so it requires no Danish gold labels.
            Blind judges see randomized Candidate A/B boundaries and never learn how either proposal was produced.
        </template>
        <template #content>
            <div class="flex flex-col space-y-5">
                <div class="rounded-md border border-green-500/30 p-3 text-sm">
                    <span class="font-semibold">Automatic live capture:</span>
                    every non-trivial source-boundary decision window reached by the non-batch translation path is
                    recorded together with its heuristic boundary and the production model/validator decision.
                </div>

                <div class="grid gap-3 md:grid-cols-4">
                    <div class="border-accent/30 rounded-md border p-3">
                        <div class="text-secondary-content/60 text-xs">Corpus samples</div>
                        <div class="text-xl font-semibold">{{ corpusCount }}</div>
                    </div>
                    <InputComponent v-model="sampleLimit" label="Samples per run" />
                    <InputComponent v-model="sampleBrowserLimit" label="Samples to inspect" />
                    <div class="flex items-end gap-2">
                        <button type="button" class="btn btn-secondary" :disabled="busy" @click="loadSamples">
                            View corpus
                        </button>
                        <button type="button" class="btn btn-ghost" :disabled="busy" @click="refreshCount">
                            Refresh
                        </button>
                    </div>
                </div>

                <div class="border-accent/30 rounded-md border p-4">
                    <div class="font-semibold">Candidate source-boundary models</div>
                    <p class="text-secondary-content/60 mb-2 text-xs">
                        Optional JSON array. Leave empty to benchmark the source-boundary model configured above.
                        Each candidate receives the identical captured source cue window and returns only unitLength.
                    </p>
                    <textarea
                        v-model="candidateModelsJson"
                        rows="7"
                        class="border-accent w-full resize-y rounded-md border bg-transparent px-4 py-2 font-mono text-xs outline-hidden"
                        :placeholder="candidatePlaceholder"></textarea>
                </div>

                <div class="border-accent/30 rounded-md border p-4">
                    <div class="font-semibold">Blind source-boundary judges</div>
                    <p class="text-secondary-content/60 mb-2 text-xs">
                        Optional JSON array. Leave empty to use the configured source-boundary validator. Judges see
                        only Candidate A and Candidate B unit lengths in pseudo-random order. High-confidence
                        punctuation/continuation cases are also used for adversarial judge calibration.
                    </p>
                    <textarea
                        v-model="judgeModelsJson"
                        rows="7"
                        class="border-accent w-full resize-y rounded-md border bg-transparent px-4 py-2 font-mono text-xs outline-hidden"
                        :placeholder="judgePlaceholder"></textarea>
                </div>

                <div class="flex flex-wrap items-center gap-5">
                    <label class="flex items-center gap-2 text-sm">
                        <input v-model="includeAdversarial" type="checkbox" class="checkbox checkbox-sm" />
                        Adversarial judge calibration
                    </label>
                    <button type="button" class="btn btn-primary" :disabled="busy" @click="runBenchmark">
                        {{ running ? 'Benchmarking…' : 'Run source-unit benchmark' }}
                    </button>
                    <button type="button" class="btn btn-ghost" :disabled="busy || corpusCount === 0" @click="clearCorpus">
                        Clear corpus
                    </button>
                </div>

                <p v-if="error" class="text-sm text-red-600">{{ error }}</p>

                <template v-if="result">
                    <div v-if="result.warnings.length" class="rounded-md border border-orange-400/40 p-3 text-sm">
                        <div v-for="warning in result.warnings" :key="warning">{{ warning }}</div>
                    </div>

                    <div>
                        <div class="mb-2 font-semibold">Candidate summary</div>
                        <div class="overflow-x-auto">
                            <table class="table table-sm">
                                <thead>
                                    <tr>
                                        <th>Candidate</th>
                                        <th>Valid</th>
                                        <th>Differs from heuristic</th>
                                        <th>Judge preference</th>
                                        <th>Judge agreement</th>
                                        <th>Adversarial pass</th>
                                        <th>Latency</th>
                                    </tr>
                                </thead>
                                <tbody>
                                    <tr v-for="candidate in result.candidates" :key="candidate.name">
                                        <td>
                                            <div class="font-semibold">{{ candidate.name }}</div>
                                            <div class="text-secondary-content/60 text-xs">{{ candidate.model }}</div>
                                        </td>
                                        <td>{{ fmt(candidate.structuralValidityPercent) }}%</td>
                                        <td>
                                            {{ candidate.disagreementSamples }}
                                            ({{ fmt(candidate.disagreementPercent) }}%)
                                        </td>
                                        <td>
                                            {{ candidate.judgeModelVotes + candidate.judgeHeuristicVotes
                                                ? `${fmt(candidate.judgePreferencePercent)}%`
                                                : '—' }}
                                        </td>
                                        <td>
                                            {{ candidate.meanJudgeAgreementPercent
                                                ? `${fmt(candidate.meanJudgeAgreementPercent)}%`
                                                : '—' }}
                                        </td>
                                        <td>
                                            {{ candidate.adversarialTrials
                                                ? `${fmt(candidate.adversarialPassPercent)}%`
                                                : '—' }}
                                        </td>
                                        <td>{{ fmt(candidate.meanBoundaryLatencyMs) }} ms</td>
                                    </tr>
                                </tbody>
                            </table>
                        </div>
                    </div>

                    <div v-if="result.judges.length">
                        <div class="mb-2 font-semibold">Judge calibration</div>
                        <div class="overflow-x-auto">
                            <table class="table table-sm">
                                <thead>
                                    <tr>
                                        <th>Judge</th>
                                        <th>Pairwise</th>
                                        <th>Decisive</th>
                                        <th>Adversarial pass</th>
                                        <th>Mean latency</th>
                                    </tr>
                                </thead>
                                <tbody>
                                    <tr v-for="judge in result.judges" :key="judge.name">
                                        <td>
                                            <div class="font-semibold">{{ judge.name }}</div>
                                            <div class="text-secondary-content/60 text-xs">{{ judge.model }}</div>
                                        </td>
                                        <td>{{ judge.pairwiseComparisons }}</td>
                                        <td>{{ judge.decisiveComparisons }}</td>
                                        <td>{{ judge.adversarialTrials ? `${fmt(judge.adversarialPassPercent)}%` : '—' }}</td>
                                        <td>{{ fmt(judge.meanLatencyMs) }} ms</td>
                                    </tr>
                                </tbody>
                            </table>
                        </div>
                    </div>

                    <details class="border-accent/30 rounded-md border p-3">
                        <summary class="cursor-pointer font-semibold">Per-sample benchmark details</summary>
                        <pre class="mt-3 max-h-[40rem] overflow-auto text-xs">{{ JSON.stringify(result.samples, null, 2) }}</pre>
                    </details>
                </template>

                <details v-if="samples.length" open class="border-accent/30 rounded-md border p-3">
                    <summary class="cursor-pointer font-semibold">Captured source-unit corpus</summary>
                    <div class="mt-3 space-y-3">
                        <details
                            v-for="sample in samples"
                            :key="sample.id"
                            class="border-accent/20 rounded-md border p-3">
                            <summary class="cursor-pointer text-sm">
                                <span class="font-semibold">#{{ sample.id }}</span>
                                — {{ sample.candidateCount }} cues — heuristic {{ sample.heuristicUnitLength }}
                                — production {{ sample.productionSelectedUnitLength ?? 'not recorded' }}
                                ({{ sample.productionSelectedMethod ?? '—' }})
                            </summary>
                            <div class="mt-3 grid gap-2 text-xs md:grid-cols-2">
                                <div>
                                    <div class="font-semibold">Candidate cues</div>
                                    <ol class="list-decimal pl-5">
                                        <li v-for="cue in sample.cues" :key="`${sample.id}-${cue.position}`">
                                            [{{ cue.position }} · {{ cue.startTime }}–{{ cue.endTime }} ms] {{ cue.text }}
                                        </li>
                                    </ol>
                                </div>
                                <pre class="max-h-64 overflow-auto">{{ JSON.stringify(sample, null, 2) }}</pre>
                            </div>
                        </details>
                    </div>
                </details>
            </div>
        </template>
    </CardComponent>
</template>

<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import services from '@/services'
import {
    ISourceUnitBenchmarkModel,
    ISourceUnitBenchmarkRunResult,
    ISourceUnitBenchmarkSampleView
} from '@/ts'
import CardComponent from '@/components/common/CardComponent.vue'
import InputComponent from '@/components/common/InputComponent.vue'

const corpusCount = ref(0)
const sampleLimit = ref('100')
const sampleBrowserLimit = ref('100')
const candidateModelsJson = ref('')
const judgeModelsJson = ref('')
const includeAdversarial = ref(true)
const running = ref(false)
const loadingSamples = ref(false)
const clearing = ref(false)
const error = ref('')
const result = ref<ISourceUnitBenchmarkRunResult | null>(null)
const samples = ref<ISourceUnitBenchmarkSampleView[]>([])

const busy = computed(() => running.value || loadingSamples.value || clearing.value)
const candidatePlaceholder = `[
  {"name":"qwen-boundary","endpoint":"http://host:8000/v1","model":"model-name"},
  {"name":"alternate-boundary","endpoint":"http://host:8001/v1","model":"model-name"}
]`
const judgePlaceholder = `[
  {"name":"judge-1","endpoint":"http://host:8010/v1","model":"judge-model"},
  {"name":"judge-2","endpoint":"http://host:8011/v1","model":"judge-model"}
]`

const fmt = (value: number): string => Number.isFinite(value) ? value.toFixed(1) : '0.0'
const errorMessage = (value: any): string => value?.data ?? value?.statusText ?? value?.message ?? 'Request failed.'

const parseModels = (value: string, label: string): ISourceUnitBenchmarkModel[] => {
    if (!value.trim()) return []
    const parsed = JSON.parse(value)
    if (!Array.isArray(parsed)) throw new Error(`${label} must be a JSON array.`)
    for (const item of parsed) {
        if (!item?.endpoint || !item?.model) throw new Error(`Every ${label.toLowerCase()} entry needs endpoint and model.`)
    }
    return parsed as ISourceUnitBenchmarkModel[]
}

const refreshCount = async () => {
    try {
        corpusCount.value = await services.sourceUnit.benchmarkCount()
    } catch (value: any) {
        error.value = errorMessage(value)
    }
}

const loadSamples = async () => {
    error.value = ''
    loadingSamples.value = true
    try {
        const limit = Math.max(1, Number.parseInt(sampleBrowserLimit.value, 10) || 100)
        samples.value = await services.sourceUnit.benchmarkSamples<ISourceUnitBenchmarkSampleView[]>(limit)
    } catch (value: any) {
        error.value = errorMessage(value)
    } finally {
        loadingSamples.value = false
    }
}

const clearCorpus = async () => {
    error.value = ''
    clearing.value = true
    try {
        await services.sourceUnit.clearBenchmarkSamples()
        corpusCount.value = 0
        samples.value = []
        result.value = null
    } catch (value: any) {
        error.value = errorMessage(value)
    } finally {
        clearing.value = false
    }
}

const runBenchmark = async () => {
    error.value = ''
    result.value = null
    running.value = true
    try {
        const candidateModels = parseModels(candidateModelsJson.value, 'Candidate models')
        const judgeModels = parseModels(judgeModelsJson.value, 'Judge models')
        const limit = Math.max(1, Number.parseInt(sampleLimit.value, 10) || 100)
        result.value = await services.sourceUnit.runBenchmark<ISourceUnitBenchmarkRunResult>({
            sampleLimit: limit,
            candidateModels,
            judgeModels,
            includeAdversarialCalibration: includeAdversarial.value
        })
        await refreshCount()
    } catch (value: any) {
        error.value = value instanceof SyntaxError ? `Invalid model JSON: ${value.message}` : errorMessage(value)
    } finally {
        running.value = false
    }
}

onMounted(refreshCount)
</script>
