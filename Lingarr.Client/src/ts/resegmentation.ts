export interface IResegmentationModelOverride {
    endpoint?: string
    model?: string
    apiKey?: string
    systemPrompt?: string
    userPrompt?: string
    timeoutSeconds?: number
}

export interface IResegmentationEvaluationRequest {
    sourceLanguage: string
    targetLanguage: string
    sourceSegments: string[]
    translatedUnit: string
    referenceSegments?: string[]
    mode?: 'deterministic' | 'model' | 'validated'
    modelOverride?: IResegmentationModelOverride
    validatorOverride?: IResegmentationModelOverride
}

export interface IResegmentationStructuralValidation {
    isValid: boolean
    countMatches: boolean
    nonEmptySegments: boolean
    textPreserved: boolean
    error?: string
}

export interface IResegmentationCandidate {
    method: string
    segments?: string[]
    validation: IResegmentationStructuralValidation
    error?: string
    latencyMs?: number
    model?: string
}

export interface IResegmentationValidatorDecision {
    winner: string
    modelScore: number
    deterministicScore: number
    reason?: string
    latencyMs?: number
    model?: string
}

export interface IResegmentationBoundaryMetrics {
    boundaryCount: number
    meanAbsoluteErrorCharacters: number
    maxAbsoluteErrorCharacters: number
    boundariesWithinFiveCharactersPercent: number
    exactSegmentMatchPercent: number
}

export interface IResegmentationEvaluationResult {
    mode: string
    selectedMethod: string
    selectedSegments: string[]
    deterministic: IResegmentationCandidate
    model?: IResegmentationCandidate
    validator?: IResegmentationValidatorDecision
    referenceValidation?: IResegmentationStructuralValidation
    deterministicReferenceMetrics?: IResegmentationBoundaryMetrics
    modelReferenceMetrics?: IResegmentationBoundaryMetrics
    fallbackReason?: string
}
