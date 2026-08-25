<template>
    <CardComponent title="Reference-free resegmentation benchmark">
        <template #description>
            Benchmark alignment models on real multi-cue translation units without creating Danish gold labels.
            New sentence-aware translations are captured automatically at the exact point after whole-unit
            translation and before any resegmentation. Blind multilingual judges, adversarial boundary checks,
            and optional backtranslation provide independent evaluation signals without requiring Danish fluency.
        </template>
        <template #content>
            <div class="flex flex-col space-y-5">
                <div class="rounded-md border border-green-500/30 p-3 text-sm">
                    <span class="font-semibold">Preferred corpus source:</span>
                    multi-cue units from new translation jobs are recorded automatically before resegmentation, so
                    the corpus contains the exact source timing slots and exact complete model translation.
                    <span class="text-secondary-content/60">
                        History harvesting below is only a bootstrap option; older jobs may predate sentence-aware
                        translation and therefore are less reliable benchmark material.
                    </span>
                </div>

                <div class="grid gap-3 md:grid-cols-4">
                    <div class="border-accent/30 rounded-md border p-3">
                        <div class="text-secondary-content/60 text-xs">Corpus samples</div>
                        <div class="text-xl font-semibold">{{ corpusCount }}</div>
                    </div>
                    <InputComponent v-model="sampleLimit" label="Samples per run" />
                    <InputComponent v-model="harvestRequestLimit" label="Historical jobs to scan" />
                    <div class="flex items-end gap-2">
                        <button type="button" class="btn btn-secondary" :disabled="busy" @click="harvest">
                            Harvest history
                        </button>
                        <button type="button" class="btn btn-ghost" :disabled="busy" @click="refreshCount">
                            Refresh
                        </button>
                    </div>
                </div>

                <div class="border-accent/30 rounded-md border p-4">
                    <div class="font-semibold">Candidate alignment models</div>
                    <p class="text-secondary-content/60 mb-2 text-xs">
                        Optional JSON array. Leave empty to benchmark the alignment model already configured above.
                        These overrides are request-scoped and are not saved. This makes it possible to compare many
                        hosted models without repeatedly changing Lingarr settings.
                    </p>
                    <textarea
                        v-model="candidateModelsJson"
                        rows="7"
                        class="border-accent w-full resize-y rounded-md border bg-transparent px-4 py-2 font-mono text-xs outline-hidden"
                        :placeholder="candidatePlaceholder"></textarea>
                </div>

                <div class="border-accent/30 rounded-md border p-4">
                    <div class="font-semibold">Blind judge models</div>
                    <p class="text-secondary-content/60 mb-2 text-xs">
                        Optional JSON array. Leave empty to use the configured validator model. Each judge sees
                        randomized Candidate A/B labels, never “model” versus “deterministic”. Multiple judges provide
                        consensus and agreement statistics. Adversarial boundary shifts measure whether a judge can
                        detect intentionally degraded segmentation.
                    </p>
                    <textarea
                        v-model="judgeModelsJson"
                        rows="7"
                        class="border-accent w-full resize-y rounded-md border bg-transparent px-4 py-2 font-mono text-xs outline-hidden"
                        :placeholder="judgePlaceholder"></textarea>
                </div>

                <div class="border-accent/30 rounded-md border p-4">
                    <div class="font-semibold">Backtranslation model — optional</div>
                    <p class="text-secondary-content/60 mb-2 text-xs">
                        Supply one OpenAI-compatible model object to backtranslate each Danish candidate segment into
                        English. Lingarr then computes source-language token-F1, same-slot versus wrong-slot margin,
                        and cross-slot leakage. This is independent of the blind judge vote and requires no Danish.
                    </p>
                    <textarea
                        v-model="backtranslationModelJson"
                        rows="5"
                        class="border-accent w-full resize-y rounded-md border bg-transparent px-4 py-2 font-mono text-xs outline-hidden"
                        :placeholder="backtranslationPlaceholder"></textarea>
                </div>

                <div class="flex flex-wrap items-center gap-5">
                    <label class="flex items-center gap-2 text-sm">
                        <input v-model="autoHarvest" type="checkbox" class="checkbox checkbox-sm" />
                        Also harvest historical jobs before benchmark
                    </label>
                    <label class="flex items-center gap-2 text-sm">
                        <input v-model="includeAdversarial" type="checkbox" class="checkbox checkbox-sm" />
                        Adversarial judge calibration
                    </label>
                    <button type="button" class="btn btn-primary" :disabled="busy" @click="runBenchmark">
                        {{ running ? 'Benchmarking…' : 'Run reference-free benchmark' }}
                    </button>
                    <button type="button" class="btn btn-ghost" :disabled="busy || corpusCount === 0" @click="clearCorpus">
                        Clear corpus
                    </button>
                </div>

                <p v-if="error" class="text-sm text-red-600">{{ error }}</p>
                <p v-if="harvestResult" class="text-secondary-content/70 text-sm">
                    History harvest scanned {{ harvestResult.requestsScanned }} completed translation jobs, found
                    {{ harvestResult.multiCueUnitsFound }} possible multi-cue units, captured
                    {{ harvestResult.newSamplesCaptured }} new samples; corpus total:
                    {{ harvestResult.totalCorpusSamples }}.
                </p>

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
                                        <th>Structural valid</th>
                                        <th>Judge preference</th>
                                        <th>Judge agreement</th>
                                        <th>Adversarial pass</th>
                                        <th>BT same-slot F1</th>
                                        <th>BT margin</th>
                                        <th>BT leakage</th>
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
                                        <td>{{ fmt(candidate.judgePreferencePercent) }}%</td>
                                        <td>{{ fmt(candidate.meanJudgeAgreementPercent) }}%</td>
                                        <td>
                                            {{ candidate.adversarialTrials ? `${fmt(candidate.adversarialPassPercent)}%` : '—' }}
                                        </td>
                                        <td>{{ optionalPercent(candidate.meanSameSlotTokenF1Percent) }}</td>
                                        <td>{{ optionalSigned(candidate.meanCrossSlotMarginPercentagePoints) }}</td>
                                        <td>{{ optionalPercent(candidate.crossSlotLeakagePercent) }}</td>
                                        <td>{{ candidate.meanAlignmentLatencyMs }} ms</td>
                                    </tr>
                                </tbody>
                            </table>
                        </div>
                    </div>

                    <div v-if="result.deterministicBaseline.backtranslationSamples > 0">
                        <div class="mb-2 font-semibold">Deterministic baseline backtranslation</div>
                        <div class="grid gap-3 md:grid-cols-3">
                            <MetricCard label="Same-slot token F1" :value="optionalPercent(result.deterministicBaseline.meanSameSlotTokenF1Percent)" />
                            <MetricCard label="Cross-slot margin" :value="optionalSigned(result.deterministicBaseline.meanCrossSlotMarginPercentagePoints)" />
                            <MetricCard label="Cross-slot leakage" :value="optionalPercent(result.deterministicBaseline.crossSlotLeakagePercent)" />
                        </div>
                    </div>

                    <div v-if="result.judges.length">
                        <div class="mb-2 font-semibold">Judge calibration</div>
                        <div class="overflow-x-auto">
                            <table class="table table-sm">
                                <thead>
                                    <tr>
                                        <th>Judge</th>
                                        <th>Pairwise comparisons</th>
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
                                        <td>{{ judge.meanLatencyMs }} ms</td>
                                    </tr>
                                </tbody>
                            </table>
                        </div>
                    </div>

                    <details class="border-accent/30 rounded-md border p-3">
                        <summary class="cursor-pointer font-semibold">Per-sample details / raw benchmark JSON</summary>
                        <pre class="mt-3 max-h-[40rem] overflow-auto text-xs">{{ JSON.stringify(result, null, 2) }}</pre>
                    </details>
                </template>
            </div>
        </template>
    </CardComponent>
</template>

<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import services from '@/services'
import {
    INamedBenchmarkModel,
    IResegmentationBenchmarkHarvestResult,
    IResegmentationBenchmarkRunResult
} from '@/ts'
import CardComponent from '@/components/common/CardComponent.vue'
import InputComponent from '@/components/common/InputComponent.vue'
import MetricCard from '@/components/common/MetricCard.vue'

const corpusCount = ref(0)
const sampleLimit = ref('50')
const harvestRequestLimit = ref('100')
const autoHarvest = ref(false)
const includeAdversarial = ref(true)
const candidateModelsJson = ref('')
const judgeModelsJson = ref('')
const backtranslationModelJson = ref('')
const running = ref(false)
const harvesting = ref(false)
const clearing = ref(false)
const error = ref('')
const harvestResult = ref<IResegmentationBenchmarkHarvestResult | null>(null)
const result = ref<IResegmentationBenchmarkRunResult | null>(null)

const busy = computed(() => running.value || harvesting.value || clearing.value)

const candidatePlaceholder = `[
  {"name":"qwen-align","endpoint":"http://host:8000/v1","model":"model-name"},
  {"name":"gemma-align","endpoint":"http://host:8001/v1","model":"model-name"}
]`
const judgePlaceholder = `[
  {"name":"judge-1","endpoint":"http://host:8010/v1","model":"judge-model"},
  {"name":"judge-2","endpoint":"http://host:8011/v1","model":"judge-model"}
]`
const backtranslationPlaceholder = `{"name":"backtranslator","endpoint":"http://host:8020/v1","model":"translation-model"}`

const fmt = (value: number): string => Number.isFinite(value) ? value.toFixed(1) : '0.0'
const optionalPercent = (value?: number): string => value === undefined || value === null ? '—' : `${fmt(value)}%`
const optionalSigned = (value?: number): string => {
    if (value === undefined || value === null) return '—'
    return `${value >= 0 ? '+' : ''}${fmt(value)} pp`
}

const errorMessage = (value: any): string =>
    value?.data ?? value?.statusText ?? value?.message ?? 'Request failed.'

const parseArray = (value: string, label: string): INamedBenchmarkModel[] => {
    if (!value.trim()) return []
    const parsed = JSON.parse(value)
    if (!Array.isArray(parsed)) throw new Error(`${label} must be a JSON array.`)
    for (const item of parsed) {
        if (!item?.endpoint || !item?.model) throw new Error(`Every ${label.toLowerCase()} entry needs endpoint and model.`)
    }
    return parsed as INamedBenchmarkModel[]
}

const parseOne = (value: string): INamedBenchmarkModel | undefined => {
    if (!value.trim()) return undefined
    const parsed = JSON.parse(value)
    if (Array.isArray(parsed) || !parsed?.endpoint || !parsed?.model) {
        throw new Error('Backtranslation model must be one JSON object with endpoint and model.')
    }
    return parsed as INamedBenchmarkModel
}

const refreshCount = async () => {
    try {
        corpusCount.value = await services.resegmentation.benchmarkCount()
    } catch (value: any) {
        error.value = errorMessage(value)
    }
}

const harvest = async () => {
    error.value = ''
    harvesting.value = true
    try {
        const limit = Math.max(1, Number.parseInt(harvestRequestLimit.value, 10) || 100)
        harvestResult.value = await services.resegmentation.harvestBenchmark<IResegmentationBenchmarkHarvestResult>(limit)
        corpusCount.value = harvestResult.value.totalCorpusSamples
    } catch (value: any) {
        error.value = errorMessage(value)
    } finally {
        harvesting.value = false
    }
}

const clearCorpus = async () => {
    error.value = ''
    clearing.value = true
    try {
        await services.resegmentation.clearBenchmarkSamples()
        corpusCount.value = 0
        harvestResult.value = null
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
        const candidateModels = parseArray(candidateModelsJson.value, 'Candidate models')
        const judgeModels = parseArray(judgeModelsJson.value, 'Judge models')
        const backtranslationModel = parseOne(backtranslationModelJson.value)
        const limit = Math.max(1, Number.parseInt(sampleLimit.value, 10) || 50)
        const harvestLimit = Math.max(1, Number.parseInt(harvestRequestLimit.value, 10) || 100)

        result.value = await services.resegmentation.runBenchmark<IResegmentationBenchmarkRunResult>({
            sampleLimit: limit,
            candidateModels,
            judgeModels,
            backtranslationModel,
            includeAdversarialCalibration: includeAdversarial.value,
            autoHarvest: autoHarvest.value,
            harvestRequestLimit: harvestLimit
        })
        if (result.value.harvest) {
            harvestResult.value = result.value.harvest
            corpusCount.value = result.value.harvest.totalCorpusSamples
        } else {
            await refreshCount()
        }
    } catch (value: any) {
        error.value = value instanceof SyntaxError ? `Invalid model JSON: ${value.message}` : errorMessage(value)
    } finally {
        running.value = false
    }
}

onMounted(refreshCount)
</script>
