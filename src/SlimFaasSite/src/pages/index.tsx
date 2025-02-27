import Layout from '../components/Layout';
import Head from 'next/head';

const Home = () => (
    <Layout>
        <Head>
            <title>Welcome to SlimFaas</title>
            <meta name="description" content="SlimFaas - The slimmest and simplest Function As A Service" />
        </Head>
        <h1>Welcome to SlimFaas</h1>
        <p>SlimFaas is a lightweight and efficient Function as a Service (FaaS) platform designed for seamless scaling and integration.</p>
    </Layout>
);

export default Home;
