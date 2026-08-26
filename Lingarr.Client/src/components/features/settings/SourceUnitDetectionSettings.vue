<template>
    <CardComponent title="Source translation-unit detection">
        <template #description>
            Decide which consecutive source subtitle cues should be combined into one linguistic unit before
            translation. This stage is independent from target-language resegmentation.
        </template>
        <template #content>
            <div class="flex flex-col space-y-5">
                <SaveNotification ref="saveNotification" />

                <div>
                    <label for="source-unit-detection-mode" class="mb-1 block text-sm font-semibold">
                        Source-unit detection mode
                    </label>
                    <select
                        id="source-unit-detection-mode"
                        v-model="mode"
                        class="border-accent w-full rounded-md border bg-transparent px-4 py-2 outline-hidden">
                        <option value="heuristic">Heuristic baseline / fallback</option>
                        <option value="model">Dedicated source-boundary model</option>
                        <option value="validated">Source-boundary model + independent validator</option>
                    </select>
                    <p class="text-secondary-content/60 mt-1 text-xs">
                        The detector may choose only a contiguous prefix of up to four nearby cues. Resumed cues,
                        the 500-character hard cap, and timing gaps over two seconds remain hard safety boundaries.
                    </p>
                </div>

                <div v-if="mode !== 'heuristic'" class="border-accent/30 rounded-md border p-4">
                    <div class="mb-3">
                        <div class="font-semibold">Dedicated source-boundary model</div>
                        <p class="text-secondary-content/60 text-xs">
                            Any OpenAI-compatible chat-completions endpoint can be used. The model decides only how
                            many leading source cues belong to the same linguistic translation unit.
                        </p>
                    </div>

                    <div class="grid gap-4 md:grid-cols-2">
                        <InputComponent v-model="endpoint" label="Endpoint" placeholder="http://host:port/v1" />
                        <InputComponent v-model="model" label="Model" placeholder="source-boundary-model" />
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
                            label="Source-boundary system prompt"
                            :rows="4"
                            allow-empty />
                        <TextAreaComponent
                            v-model="userPrompt"
                            label="Source-boundary user prompt"
                            :rows="9"
                            :required-placeholders="['{sourceCuesJson}', '{candidateCount}']"
                            @update:validation="(value) => (userPromptValid = value)" />
                        <p class="text-secondary-content/60 text-xs">
                            Available placeholders: <code>{sourceLanguage}</code>, <code>{candidateCount}</code>,
                            and <code>{sourceCuesJson}</code>.
                        </p>
                    </div>
                </div>

                <div v-if="mode === 'validated'" class="border-accent/30 rounded-md border p-4">
                    <div class="mb-3">
                        <div class="font-semibold">Independent source-boundary validator / judge</div>
                        <p class="text-secondary-content/60 text-xs">
                            Compares the two boundaries only when they disagree. Candidate origins are hidden and
                            A/B order is randomized before the judge sees them to reduce evaluator bias.
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
                            placeholder="judge-model" />
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
                            :rows="9"
                            :required-placeholders="[
                                '{sourceCuesJson}',
                                '{candidateAUnitLength}',
                                '{candidateBUnitLength}'
                            ]"
                            @update:validation="(value) => (validatorUserPromptValid = value)" />
                        <p class="text-secondary-content/60 text-xs">
                            Validator placeholders additionally include <code>{candidateAUnitLength}</code> and
                            <code>{candidateBUnitLength}</code>. Candidate identity is mapped back only after judging.
                        </p>
                    </div>
                </div>
            </div>
        </template>
    </CardComponent>
</template>

<script setup lang="ts">
import { computed, ref } from 'vue'
import { useSettingStore } from '@/store/setting'
import { ENCRYPTED_SETTINGS, INPUT_TYPE, INPUT_VALIDATION_TYPE, SETTINGS } from '@/ts'
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

const mode = bindSetting(SETTINGS.SOURCE_UNIT_DETECTION_MODE)
const endpoint = bindSetting(SETTINGS.SOURCE_UNIT_DETECTION_ENDPOINT)
const model = bindSetting(SETTINGS.SOURCE_UNIT_DETECTION_MODEL)
const systemPrompt = bindSetting(SETTINGS.SOURCE_UNIT_DETECTION_SYSTEM_PROMPT)
const validatorEndpoint = bindSetting(SETTINGS.SOURCE_UNIT_DETECTION_VALIDATOR_ENDPOINT)
const validatorModel = bindSetting(SETTINGS.SOURCE_UNIT_DETECTION_VALIDATOR_MODEL)
const validatorSystemPrompt = bindSetting(SETTINGS.SOURCE_UNIT_DETECTION_VALIDATOR_SYSTEM_PROMPT)

const timeoutSeconds = computed({
    get: () => (settingsStore.getSetting(SETTINGS.SOURCE_UNIT_DETECTION_TIMEOUT_SECONDS) as string) ?? '120',
    set: (value: string) => {
        settingsStore.updateSetting(SETTINGS.SOURCE_UNIT_DETECTION_TIMEOUT_SECONDS, value, timeoutValid.value)
        if (timeoutValid.value) saveNotification.value?.show()
    }
})

const userPrompt = computed({
    get: () => (settingsStore.getSetting(SETTINGS.SOURCE_UNIT_DETECTION_USER_PROMPT) as string) ?? '',
    set: (value: string) => {
        settingsStore.updateSetting(SETTINGS.SOURCE_UNIT_DETECTION_USER_PROMPT, value, userPromptValid.value)
        if (userPromptValid.value) saveNotification.value?.show()
    }
})

const validatorUserPrompt = computed({
    get: () =>
        (settingsStore.getSetting(SETTINGS.SOURCE_UNIT_DETECTION_VALIDATOR_USER_PROMPT) as string) ?? '',
    set: (value: string) => {
        settingsStore.updateSetting(
            SETTINGS.SOURCE_UNIT_DETECTION_VALIDATOR_USER_PROMPT,
            value,
            validatorUserPromptValid.value
        )
        if (validatorUserPromptValid.value) saveNotification.value?.show()
    }
})

const apiKey = computed({
    get: () =>
        (settingsStore.getEncryptedSetting(ENCRYPTED_SETTINGS.SOURCE_UNIT_DETECTION_API_KEY) as string) ?? '',
    set: (value: string) => {
        settingsStore.updateEncryptedSetting(ENCRYPTED_SETTINGS.SOURCE_UNIT_DETECTION_API_KEY, value)
        saveNotification.value?.show()
    }
})

const validatorApiKey = computed({
    get: () =>
        (settingsStore.getEncryptedSetting(
            ENCRYPTED_SETTINGS.SOURCE_UNIT_DETECTION_VALIDATOR_API_KEY
        ) as string) ?? '',
    set: (value: string) => {
        settingsStore.updateEncryptedSetting(
            ENCRYPTED_SETTINGS.SOURCE_UNIT_DETECTION_VALIDATOR_API_KEY,
            value
        )
        saveNotification.value?.show()
    }
})
</script>
