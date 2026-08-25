import { AxiosError, AxiosResponse, AxiosStatic } from 'axios'
import {
    IResegmentationBenchmarkRunRequest,
    IResegmentationEvaluationRequest,
    IResegmentationService
} from '@/ts'

const service = (http: AxiosStatic, resource = '/api/resegmentation'): IResegmentationService => ({
    evaluate<T>(request: IResegmentationEvaluationRequest): Promise<T> {
        return new Promise((resolve, reject) => {
            http.post(`${resource}/evaluate`, request)
                .then((response: AxiosResponse<T>) => {
                    resolve(response.data)
                })
                .catch((error: AxiosError) => {
                    reject(error.response)
                })
        })
    },

    benchmarkCount(): Promise<number> {
        return new Promise((resolve, reject) => {
            http.get(`${resource}/benchmark/count`)
                .then((response: AxiosResponse<number>) => resolve(response.data))
                .catch((error: AxiosError) => reject(error.response))
        })
    },

    harvestBenchmark<T>(maxRequests = 100): Promise<T> {
        return new Promise((resolve, reject) => {
            http.post(`${resource}/benchmark/harvest`, null, { params: { maxRequests } })
                .then((response: AxiosResponse<T>) => resolve(response.data))
                .catch((error: AxiosError) => reject(error.response))
        })
    },

    runBenchmark<T>(request: IResegmentationBenchmarkRunRequest): Promise<T> {
        return new Promise((resolve, reject) => {
            http.post(`${resource}/benchmark/run`, request)
                .then((response: AxiosResponse<T>) => resolve(response.data))
                .catch((error: AxiosError) => reject(error.response))
        })
    },

    clearBenchmarkSamples(): Promise<number> {
        return new Promise((resolve, reject) => {
            http.delete(`${resource}/benchmark/samples`)
                .then((response: AxiosResponse<number>) => resolve(response.data))
                .catch((error: AxiosError) => reject(error.response))
        })
    }
})

export const resegmentationService = (axios: AxiosStatic): IResegmentationService => {
    return service(axios)
}
