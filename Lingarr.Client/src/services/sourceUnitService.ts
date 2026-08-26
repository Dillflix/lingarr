import { AxiosError, AxiosResponse, AxiosStatic } from 'axios'
import { ISourceUnitBenchmarkRunRequest, ISourceUnitService } from '@/ts'

const service = (http: AxiosStatic, resource = '/api/source-unit'): ISourceUnitService => ({
    benchmarkCount(): Promise<number> {
        return new Promise((resolve, reject) => {
            http.get(`${resource}/benchmark/count`)
                .then((response: AxiosResponse<number>) => resolve(response.data))
                .catch((error: AxiosError) => reject(error.response))
        })
    },

    benchmarkSamples<T>(limit = 100, sourceLanguage?: string): Promise<T> {
        return new Promise((resolve, reject) => {
            http.get(`${resource}/benchmark/samples`, { params: { limit, sourceLanguage } })
                .then((response: AxiosResponse<T>) => resolve(response.data))
                .catch((error: AxiosError) => reject(error.response))
        })
    },

    runBenchmark<T>(request: ISourceUnitBenchmarkRunRequest): Promise<T> {
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

export const sourceUnitService = (axios: AxiosStatic): ISourceUnitService => service(axios)
