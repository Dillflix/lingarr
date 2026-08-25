<template>
    <CardComponent title="Translation-unit resegmentation">
        <template #description>
            Configure how complete translated units are aligned back to their original subtitle timing slots.
            The translation model and alignment model are independent.
        </template>
        <template #content>
            <div class="flex flex-col space-y-5">
                <SaveNotification ref="saveNotification" />

                <div>
                    <label for="resegmentation-mode" class="mb-1 block text-sm font-semibold">
                        Resegmentation mode
                    </label>
                    <select
                        id="resegmentation-mode"
                        v-model="mode"
                        class="border-accent w-full rounded-md border bg-transparent px-4 py-2 outline-hidden">
                        <option value="deterministic">Deterministic baseline / fallback</option>
                        <option value="model">Dedicated alignment model</option>
                        <option value="validated">Alignment model + independent validator</option>
                    </select>
                    <p class="text-secondary-content/60 mt-1 text-xs">
                        Deterministic splitting is always computed as a baseline and emergency fallback.
                        Model output is accepted only when it returns exactly the required number of segments
                        and preserves every translated token in order, modulo boundary whitespace.
                    </p>
                </div>

                <div v-if="mode !== 'deterministic'" class="flex flex-col space-y-4">
                    <div class="border-accent/30 rounded-md border p-4">
                        <div class="mb-3">
                            <div class="font-semibold">Dedicated alignment model</div>
                            <p class="text-secondary-content/60 text-xs">
                                Any OpenAI-compatible chat-completions endpoint can be used. This model never
                                translates; it only assigns the already-translated text to source timing slots.
                            </p>
                        </div>

                        <div class="grid gap-4 md:grid-cols-2">
                            <InputComponent
                                v-model="endpoint"
                                label="Endpoint"
                                placeholder="http://host:port/v1" />
                            <InputComponent
                                v-model="model"
                                label="Model"
                                placeholder="alignment-model-name" />
                            <InputComponent
                                v-model="apiKey"
                                :type="INPUT_TYPE.PASSWORD"
                                label="API key"
                                placeholder="Optional" />
                            <InputComponent
                                v-model="timeoutSeconds"
                                :type="INPUT_TYPE.NUMBER"
                                :validation-type="INPUT_VALIDATION_TYPE.NUMBER"
                                label="Timeout (seconds)"
                                @update:validation="(value) => (timeoutValid = value)" />
                        </div>

                        <div class="mt-4 flex flex-col space-y-4">
                            <TextAreaComponent
                                v-model="systemPrompt"
                                label="Alignment system prompt"
                                :rows="4"
                                allow-empty />
                            <TextAreaComponent
                                v-model="userPrompt"
                                label="Alignment user prompt"
                                :rows="9"
                                :required-placeholders="[
                                    '{sourceSegmentsJson}',
                                    '{translatedUnit}',
                                    '{segmentCount}'
                                ]"
                                @update:validation="(value) => (userPromptValid = value)" />
                            <p class="text-secondary-content/60 text-xs">
                                Available placeholders: <code>{sourceLanguage}</code>,
                                <code>{targetLanguage}</code>, <code>{segmentCount}</code>,
                                <code>{sourceSegmentsJson}</code>, and <code>{translatedUnit}</code>.
                            </p>
                        </div>
                    </div>
                </div>

                <div v-if="mode === 'validated'" class="border-accent/30 rounded-md border p-4">
                    <div class="mb-3">
                        <div class="font-semibold">Independent validator / judge</div>
                        <p class="text-secondary-content/60 text-xs">
                            Configure a separate model to compare the semantic alignment from the dedicated
                            model against the deterministic baseline. It can be a different endpoint and model.
                        </p>
                    </div>

                    <div class="grid gap-4 md:grid-cols-2">
                        <InputComponent
                            v-model="validatorEndpoint"
                            label="Validator endpoint"
                            placeholder="http://host:port/v1" />
                        <InputComponent
                            v-model="validatorModel"
                            label="Validator model"
                            placeholder="judge-model-name" />
                        <InputComponent
                            v-model="validatorApiKey"
                            :type="INPUT_TYPE.PASSWORD"
                            label="Validator API key"
                            placeholder="Optional" />
                    </div>

                    <div class="mt-4 flex flex-col space-y-4">
                        <TextAreaComponent
                            v-model="validatorSystemPrompt"
                            label="Validator system prompt"
                            :rows="4"
                            allow-empty />
                        <TextAreaComponent
                            v-model="validatorUserPrompt"
                            label="Validator user prompt"
                            :rows="10"
                            :required-placeholders="[
                                '{sourceSegmentsJson}',
                                '{translatedUnit}',
                                '{modelSegmentsJson}',
                                '{deterministicSegmentsJson}'
                            ]"
                            @update:validation="(value) => (validatorUserPromptValid = value)" />
                        <p class="text-secondary-content/60 text-xs">
                            Validator placeholders additionally include <code>{modelSegmentsJson}</code> and
                            <code>{deterministicSegmentsJson}</code>.
                        </p>
                    </div>
                </div>

                <div class="border-accent/30 rounded-md border p-4">
                    <div class="mb-3">
                        <div class="font-semibold">Empirical resegmentation test bench</div>
                        <p class="text-secondary-content/60 text-xs">
                            Run the deterministic baseline and the configured model on the same translated unit.
                            Supply human reference segments to calculate boundary error and exact-segment metrics.
                            Validated mode also invokes the independent judge. The same capability is available at
                            <code>POST /api/resegmentation/evaluate</code> for automated benchmark harnesses.
                        </p>
                    </div>

                    <div class="grid gap-4 md:grid-cols-3">
                        <InputComponent
                            v-model="evaluationSourceLanguage"
                            label="Source language"
                            placeholder="English" />
                        <InputComponent
                            v-model="evaluationTargetLanguage"
                            label="Target language"
                            placeholder="Danish" />
                        <div>
                            <label for="evaluation-mode" class="mb-1 block text-sm">Evaluation mode</label>
                            <select
                                id="evaluation-mode"
                                v-model="evaluationMode"
                                class="border-accent w-full rounded-md border bg-transparent px-4 py-2 outline-hidden">
                                <option value="deterministic">Deterministic only</option>
                                <option value="model">Model vs deterministic</option>
                                <option value="validated">Model vs deterministic + judge</option>
                            </select>
                        </div>
                    </div>

                    <div class="mt-4 grid gap-4 lg:grid-cols-2">
                        <div>
                            <label for="evaluation-source-segments" class="mb-1 block text-sm">
                                Source segments — one timing slot per line
                            </label>
                            <textarea
                                id="evaluation-source-segments"
                                v-model="evaluationSourceSegments"
                                rows="7"
                                class="border-accent w-full resize-y rounded-md border bg-transparent px-4 py-2 outline-hidden"
                                placeholder="I was spurred on by my 15-year-old daughter,&#10;who I was surprised to discover&#10;had seen Bonnie all over her social media."></textarea>
                        </div>
                        <div>
                            <label for="evaluation-reference-segments" class="mb-1 block text-sm">
                                Human reference target segments — optional, one per line
                            </label>
                            <textarea
                                id="evaluation-reference-segments"
                                v-model="evaluationReferenceSegments"
                                rows="7"
                                class="border-accent w-full resize-y rounded-md border bg-transparent px-4 py-2 outline-hidden"
                                placeholder="Leave empty unless you have a gold/reference segmentation"></textarea>
                        </div>
                    </div>

                    <div class="mt-4">
                        <label for="evaluation-translated-unit" class="mb-1 block text-sm">
                            Complete target translation
                        </label>
                        <textarea
                            id="evaluation-translated-unit"
                            v-model="evaluationTranslatedUnit"
                            rows="5"
                            class="border-accent w-full resize-y rounded-md border bg-transparent px-4 py-2 outline-hidden"
                            placeholder="The complete translation produced before resegmentation"></textarea>
                    </div>

                    <div class="mt-4 flex items-center gap-3">
                        <button
                            type="button"
                            class="btn btn-primary"
                            :disabled="evaluationRunning"
                            @click="runEvaluation">
                            {{ evaluationRunning ? 'Evaluating…' : 'Run comparison' }}
                        </button>
                        <span v-if="evaluationError" class="text-sm text-red-600">
                            {{ evaluationError }}
                        </span>
                    </div>

                    <div v-if="evaluationResult" class="mt-4 space-y-3">
                        <div class="grid gap-3 md:grid-cols-3">
                            <div class="border-accent/30 rounded-md border p-3">
                                <div class="text-secondary-content/60 text-xs">Selected</div>
                                <div class="font-semibold">{{ evaluationResult.selectedMethod }}</div>
                            </div>
                            <div class="border-accent/30 rounded-md border p-3">
                                <div class="text-secondary-content/60 text-xs">Model structural validation</div>
                                <div class="font-semibold">
                                    {{ evaluationResult.model?.validation.isValid ?? 'not run' }}
                                </div>
                            </div>
                            <div class="border-accent/30 rounded-md border p-3">
                                <div class="text-secondary-content/60 text-xs">Judge winner</div>
                                <div class="font-semibold">
                                    {{ evaluationResult.validator?.winner ?? 'not run' }}
                                </div>
                            </div>
                        </div>
                        <pre class="border-accent/30 max-h-96 overflow-auto rounded-md border p-3 text-xs">{{
                            JSON.stringify(evaluationResult, null, 2)
                        }}</pre>
                    </div>
                </div>
            </div>
        </template>
    </CardComponent>
</template>

<script setup lang="ts">
import { computed, ref } from 'vue'
import services from '@/services'
import { useSettingStore } from '@/store/setting'
import {
    ENCRYPTED_SETTINGS,
    IResegmentationEvaluationResult,
    INPUT_TYPE,
    INPUT_VALIDATION_TYPE,
    SETTINGS
} from '@/ts'
import CardComponent from '@/components/common/CardComponent.vue'
import InputComponent from '@/components/common/InputComponent.vue'
import SaveNotification from '@/components/common/SaveNotification.vue'
import TextAreaComponent from '@/components/common/TextAreaComponent.vue'

const settingsStore = useSettingStore()
const saveNotification = ref<InstanceType<typeof SaveNotification> | null>(null)
const timeoutValid = ref(true)
const userPromptValid = ref(true)
const validatorUserPromptValid = ref(true)

const bindSetting = (key: keyof typeof settingsStore.settings) =>
    computed({
        get: () => (settingsStore.getSetting(key) as string) ?? '',
        set: (value: string) => {
            settingsStore.updateSetting(key, value, true)
            saveNotification.value?.show()
        }
    })

const mode = bindSetting(SETTINGS.RESEGMENTATION_MODE)
const endpoint = bindSetting(SETTINGS.RESEGMENTATION_ENDPOINT)
const model = bindSetting(SETTINGS.RESEGMENTATION_MODEL)
const systemPrompt = bindSetting(SETTINGS.RESEGMENTATION_SYSTEM_PROMPT)
const validatorEndpoint = bindSetting(SETTINGS.RESEGMENTATION_VALIDATOR_ENDPOINT)
const validatorModel = bindSetting(SETTINGS.RESEGMENTATION_VALIDATOR_MODEL)
const validatorSystemPrompt = bindSetting(SETTINGS.RESEGMENTATION_VALIDATOR_SYSTEM_PROMPT)

const timeoutSeconds = computed({
    get: () => (settingsStore.getSetting(SETTINGS.RESEGMENTATION_TIMEOUT_SECONDS) as string) ?? '120',
    set: (value: string) => {
        settingsStore.updateSetting(SETTINGS.RESEGMENTATION_TIMEOUT_SECONDS, value, timeoutValid.value)
        if (timeoutValid.value) saveNotification.value?.show()
    }
})

const userPrompt = computed({
    get: () => (settingsStore.getSetting(SETTINGS.RESEGMENTATION_USER_PROMPT) as string) ?? '',
    set: (value: string) => {
        settingsStore.updateSetting(SETTINGS.RESEGMENTATION_USER_PROMPT, value, userPromptValid.value)
        if (userPromptValid.value) saveNotification.value?.show()
    }
})

const validatorUserPrompt = computed({
    get: () =>
        (settingsStore.getSetting(SETTINGS.RESEGMENTATION_VALIDATOR_USER_PROMPT) as string) ?? '',
    set: (value: string) => {
        settingsStore.updateSetting(
            SETTINGS.RESEGMENTATION_VALIDATOR_USER_PROMPT,
            value,
            validatorUserPromptValid.value
        )
        if (validatorUserPromptValid.value) saveNotification.value?.show()
    }
})

const apiKey = computed({
    get: () =>
        (settingsStore.getEncryptedSetting(ENCRYPTED_SETTINGS.RESEGMENTATION_API_KEY) as string) ?? '',
    set: (value: string) => {
        settingsStore.updateEncryptedSetting(ENCRYPTED_SETTINGS.RESEGMENTATION_API_KEY, value)
        saveNotification.value?.show()
    }
})

const validatorApiKey = computed({
    get: () =>
        (settingsStore.getEncryptedSetting(
            ENCRYPTED_SETTINGS.RESEGMENTATION_VALIDATOR_API_KEY
        ) as string) ?? '',
    set: (value: string) => {
        settingsStore.updateEncryptedSetting(
            ENCRYPTED_SETTINGS.RESEGMENTATION_VALIDATOR_API_KEY,
            value
        )
        saveNotification.value?.show()
    }
})

const evaluationSourceLanguage = ref('')
const evaluationTargetLanguage = ref('')
const evaluationMode = ref<'deterministic' | 'model' | 'validated'>('model')
const evaluationSourceSegments = ref('')
const evaluationReferenceSegments = ref('')
const evaluationTranslatedUnit = ref('')
const evaluationRunning = ref(false)
const evaluationError = ref('')
const evaluationResult = ref<IResegmentationEvaluationResult | null>(null)

const splitLines = (value: string): string[] =>
    value
        .split(/\r?\n/)
        .map((line) => line.trim())
        .filter((line) => line.length > 0)

const runEvaluation = async () => {
    evaluationError.value = ''
    evaluationResult.value = null

    const sourceSegments = splitLines(evaluationSourceSegments.value)
    const referenceSegments = splitLines(evaluationReferenceSegments.value)

    if (!evaluationSourceLanguage.value.trim() || !evaluationTargetLanguage.value.trim()) {
        evaluationError.value = 'Source and target languages are required.'
        return
    }
    if (sourceSegments.length === 0) {
        evaluationError.value = 'Enter at least one source segment.'
        return
    }
    if (!evaluationTranslatedUnit.value.trim()) {
        evaluationError.value = 'Enter the complete target translation.'
        return
    }
    if (referenceSegments.length > 0 && referenceSegments.length !== sourceSegments.length) {
        evaluationError.value = 'Reference segment count must match source segment count.'
        return
    }

    evaluationRunning.value = true
    try {
        evaluationResult.value = await services.resegmentation.evaluate<IResegmentationEvaluationResult>({
            sourceLanguage: evaluationSourceLanguage.value.trim(),
            targetLanguage: evaluationTargetLanguage.value.trim(),
            sourceSegments,
            translatedUnit: evaluationTranslatedUnit.value.trim(),
            referenceSegments: referenceSegments.length > 0 ? referenceSegments : undefined,
            mode: evaluationMode.value
        })
    } catch (error: any) {
        evaluationError.value =
            error?.data ?? error?.statusText ?? error?.message ?? 'Resegmentation evaluation failed.'
    } finally {
        evaluationRunning.value = false
    }
}
</script>
